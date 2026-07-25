//! One-time GPU work turning an uploaded equirect environment texture into a
//! GGX-prefiltered specular cubemap (#144): convert equirect->cubemap, then
//! importance-sample each mip/face pair from that cubemap. Runs once per
//! environment texture upload (see `assets.uploadEnvironment`), not per
//! frame. Split into its own file for the same reason as
//! `postprocess_pipeline.zig` — `assets.zig`/`pipeline.zig` are already near
//! this repo's file-size convention.
const std = @import("std");
const gpu = @import("gpu");
const state = @import("state.zig");
const types = @import("types.zig");
const pipeline = @import("pipeline.zig");

const c = gpu.c;

/// Face size of the un-prefiltered equirect->cubemap conversion target.
const BASE_SIZE: u32 = 256;
/// Face size and mip count of the prefiltered specular cubemap. Mip `m`'s
/// roughness is `m / (PREFILTER_MIP_COUNT - 1)`, matching `max_lod` scaling
/// in `scene.frag.glsl`.
const PREFILTER_SIZE: u32 = 128;
const PREFILTER_MIP_COUNT: u32 = 6;

pub const Prefiltered = struct {
    base_cubemap: *c.SDL_GPUTexture,
    prefiltered_cubemap: *c.SDL_GPUTexture,
    /// Mip levels in `prefiltered_cubemap` — the `max_lod` scale
    /// `scene.frag.glsl` multiplies `roughness` by (via `env_params.y`).
    mip_count: u32,
};

/// Shared fixed-function setup for a fullscreen-triangle-into-cubemap-face
/// pipeline: no vertex buffers, no depth attachment, HDR float color target.
fn buildPipeline(dev: *c.SDL_GPUDevice, frag_spv: []const u8, num_samplers: u32) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");

    const vert = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = vert_spv.len,
        .code = vert_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_VERTEX,
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 0,
        .props = 0,
    }) orelse return error.VertShader;
    defer c.SDL_ReleaseGPUShader(dev, vert);

    const frag = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = frag_spv.len,
        .code = frag_spv.ptr,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = num_samplers,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = state.HDR_COLOR_FORMAT,
        .blend_state = std.mem.zeroes(c.SDL_GPUColorTargetBlendState),
    };

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 0,
        .vertex_buffer_descriptions = null,
        .num_vertex_attributes = 0,
        .vertex_attributes = null,
    };
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = c.SDL_GPU_CULLMODE_NONE,
        .front_face = c.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
        .depth_bias_constant_factor = 0,
        .depth_bias_clamp = 0,
        .depth_bias_slope_factor = 0,
        .enable_depth_bias = false,
        .enable_depth_clip = true,
        .padding1 = 0,
        .padding2 = 0,
    };
    info.depth_stencil_state = std.mem.zeroes(c.SDL_GPUDepthStencilState);
    info.multisample_state.sample_count = c.SDL_GPU_SAMPLECOUNT_1;
    info.target_info = .{
        .num_color_targets = 1,
        .color_target_descriptions = &color_desc,
        .depth_stencil_format = 0,
        .has_depth_stencil_target = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

/// Lazily creates the two prefilter pipelines + cubemap sampler, caching them
/// in `state` (same lifetime as `state.skybox_pipeline` etc — released once
/// in `root.deinit`).
fn ensurePipelines(dev: *c.SDL_GPUDevice) !void {
    if (state.ibl_equirect_to_cubemap_pipeline == null) {
        const frag_spv = @embedFile("shaders/compiled/equirect_to_cubemap.frag.spv");
        state.ibl_equirect_to_cubemap_pipeline = try buildPipeline(dev, frag_spv, 1);
    }
    if (state.ibl_prefilter_pipeline == null) {
        const frag_spv = @embedFile("shaders/compiled/prefilter_specular.frag.spv");
        state.ibl_prefilter_pipeline = try buildPipeline(dev, frag_spv, 1);
    }
    if (state.cubemap_sampler == null) {
        state.cubemap_sampler = try pipeline.createCubemapSampler(dev);
    }
}

/// Render one fullscreen triangle into `dst`'s (`mip`, `face`) subresource,
/// sampling `src_tex` at slot 0 and pushing `ub` as the fragment uniform.
fn facePass(
    cmd: *c.SDL_GPUCommandBuffer,
    pl: *c.SDL_GPUGraphicsPipeline,
    dst: *c.SDL_GPUTexture,
    mip: u32,
    face: u32,
    size: u32,
    src_tex: *c.SDL_GPUTexture,
    sampler: *c.SDL_GPUSampler,
    ub: *const anyopaque,
    ub_size: u32,
) void {
    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = dst;
    color_info.mip_level = mip;
    color_info.layer_or_depth_plane = face;
    color_info.load_op = c.SDL_GPU_LOADOP_DONT_CARE;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, null) orelse return;
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(size),
        .h = @floatFromInt(size),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    const binding = c.SDL_GPUTextureSamplerBinding{ .texture = src_tex, .sampler = sampler };
    c.SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
    c.SDL_PushGPUFragmentUniformData(cmd, 0, ub, ub_size);
    c.SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
    c.SDL_EndGPURenderPass(pass);
}

/// Convert `equirect_tex` to a base cubemap, then GGX-prefilter it into a
/// mipped specular cubemap. Both textures are returned for the caller to
/// cache (see `assets.uploadEnvironment` / `state.EnvironmentData`) — this
/// runs once per environment upload, not per frame.
pub fn prefilterEnvironment(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    equirect_tex: *c.SDL_GPUTexture,
    equirect_sampler: *c.SDL_GPUSampler,
) !Prefiltered {
    try ensurePipelines(dev);
    const conv_pl = state.ibl_equirect_to_cubemap_pipeline orelse return error.Pipeline;
    const filt_pl = state.ibl_prefilter_pipeline orelse return error.Pipeline;
    const cube_smp = state.cubemap_sampler orelse return error.SamplerCreate;

    const base = try pipeline.createCubemapTexture(dev, BASE_SIZE, 1);
    errdefer c.SDL_ReleaseGPUTexture(dev, base);
    for (0..6) |face| {
        const ub = types.EquirectToCubemapUB{ .face = .{ @floatFromInt(face), 0, 0, 0 } };
        facePass(cmd, conv_pl, base, 0, @intCast(face), BASE_SIZE, equirect_tex, equirect_sampler, &ub, @sizeOf(types.EquirectToCubemapUB));
    }

    const prefiltered = try pipeline.createCubemapTexture(dev, PREFILTER_SIZE, PREFILTER_MIP_COUNT);
    errdefer c.SDL_ReleaseGPUTexture(dev, prefiltered);
    for (0..PREFILTER_MIP_COUNT) |mip| {
        const roughness: f32 = @as(f32, @floatFromInt(mip)) / @as(f32, @floatFromInt(PREFILTER_MIP_COUNT - 1));
        const mip_size = @max(PREFILTER_SIZE >> @intCast(mip), 1);
        for (0..6) |face| {
            const ub = types.PrefilterUB{ .face_roughness = .{ @floatFromInt(face), roughness, 0, 0 } };
            facePass(cmd, filt_pl, prefiltered, @intCast(mip), @intCast(face), mip_size, base, cube_smp, &ub, @sizeOf(types.PrefilterUB));
        }
    }

    return .{ .base_cubemap = base, .prefiltered_cubemap = prefiltered, .mip_count = PREFILTER_MIP_COUNT };
}
