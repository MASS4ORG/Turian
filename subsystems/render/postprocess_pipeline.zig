//! One-time GPU resource creation for the post-process stack: the HDR resolve
//! target, bloom mip textures, and the 4 fullscreen graphics pipelines
//! (threshold, downsample, upsample, composite). Mirrors `pipeline.zig`'s
//! role for the rest of the renderer; split into its own file since
//! `pipeline.zig` is already near this repo's file-size convention.
const std = @import("std");
const gpu = @import("gpu");
const state = @import("state.zig");
const pipeline = @import("pipeline.zig");

const c = gpu.c;

/// Create the single-sample HDR resolve target the main pass renders into (or
/// MSAA-resolves into) at `w`x`h`. Always sample count 1 — this is the
/// resolve destination, not the multisampled color.
pub fn createHdrColor(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.HDR_COLOR_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.HdrColorCreate;
}

/// Create one bloom mip-chain level at `w`x`h` (HDR format, sampler + color target).
pub fn createBloomMip(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.HDR_COLOR_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.BloomMipCreate;
}

/// 1x1 black texture substituted for the bloom source when bloom is off or
/// its pipelines failed to create — always multiplied by `bloom_intensity`,
/// but kept black so a future bug can't silently wash frames out white.
pub fn createBlackTex(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice) !*c.SDL_GPUTexture {
    return pipeline.createSolidTexture(cmd, dev, .{ 0, 0, 0, 255 });
}

/// Shared fixed-function setup for every fullscreen post-process pipeline: no
/// vertex buffers (the `gl_VertexIndex` trick), no depth attachment, sample
/// count 1 (these passes never run under the scene's MSAA sample count).
fn buildPipeline(
    dev: *c.SDL_GPUDevice,
    vert_spv: []const u8,
    frag_spv: []const u8,
    num_samplers: u32,
    num_uniform_buffers: u32,
    color_format: c.SDL_GPUTextureFormat,
    blend: c.SDL_GPUColorTargetBlendState,
) !*c.SDL_GPUGraphicsPipeline {
    const vert = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = vert_spv.len,
        .code = vert_spv.ptr,
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
        .num_uniform_buffers = num_uniform_buffers,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = color_format,
        .blend_state = blend,
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

/// Bright-pass extract: 1 sampler (`hdr_color`), 1 uniform buffer (threshold/knee).
pub fn createThresholdPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/bloom_threshold.frag.spv");
    return buildPipeline(dev, vert_spv, frag_spv, 1, 1, state.HDR_COLOR_FORMAT, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}

/// 13-tap downsample: 1 sampler, no uniforms (fixed kernel).
pub fn createDownsamplePipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/bloom_downsample.frag.spv");
    return buildPipeline(dev, vert_spv, frag_spv, 1, 0, state.HDR_COLOR_FORMAT, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}

/// Tent-filter upsample: 1 sampler, 1 uniform buffer (radius), additive blend
/// so it accumulates onto the existing downsample content one mip down.
pub fn createUpsamplePipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/bloom_upsample.frag.spv");
    return buildPipeline(dev, vert_spv, frag_spv, 1, 1, state.HDR_COLOR_FORMAT, pipeline.blendStateFor(.additive));
}

/// Final combine: 2 samplers (`hdr_scene`, `bloom_tex`), 1 uniform buffer
/// (vignette/grading/bloom params). Writes the caller's UNORM `color_tex`.
pub fn createCompositePipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/composite.frag.spv");
    return buildPipeline(dev, vert_spv, frag_spv, 2, 1, c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}
