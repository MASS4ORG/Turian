//! GPU pipeline, sampler, and helper-texture creation (SDL3 GPU + SPIR-V).
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");

const c = gpu.c;

pub fn createSampler(dev: *c.SDL_GPUDevice) !*c.SDL_GPUSampler {
    return c.SDL_CreateGPUSampler(dev, &c.SDL_GPUSamplerCreateInfo{
        .min_filter = c.SDL_GPU_FILTER_LINEAR,
        .mag_filter = c.SDL_GPU_FILTER_LINEAR,
        .mipmap_mode = c.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR,
        .address_mode_u = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .address_mode_v = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .address_mode_w = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .mip_lod_bias = 0,
        .max_anisotropy = 1,
        .compare_op = c.SDL_GPU_COMPAREOP_NEVER,
        .min_lod = 0,
        .max_lod = 1000,
        .enable_anisotropy = false,
        .enable_compare = false,
        .padding1 = 0,
        .padding2 = 0,
        .props = 0,
    }) orelse error.SamplerCreate;
}

/// Create a 1x1 RGBA texture used as a sampler default for unbound material maps.
pub fn createSolidTexture(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, rgba: [4]u8) !*c.SDL_GPUTexture {
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = 4,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    const p: [*]u8 = @ptrCast(@alignCast(
        c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
    ));
    p[0] = rgba[0];
    p[1] = rgba[1];
    p[2] = rgba[2];
    p[3] = rgba[3];
    c.SDL_UnmapGPUTransferBuffer(dev, tb);

    const tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER,
        .width = 1,
        .height = 1,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = 0, .pixels_per_row = 1, .rows_per_layer = 1 }, &c.SDL_GPUTextureRegion{ .texture = tex, .mip_level = 0, .layer = 0, .x = 0, .y = 0, .z = 0, .w = 1, .h = 1, .d = 1 }, false);
    c.SDL_EndGPUCopyPass(cp);
    return tex;
}

/// Create a 1x1-per-face RGBA cubemap used as a sampler default when no
/// environment (or no prefiltered specular cubemap yet) is bound.
pub fn createSolidCubemap(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, rgba: [4]u8) !*c.SDL_GPUTexture {
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = 4 * 6,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    const p: [*]u8 = @ptrCast(@alignCast(
        c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
    ));
    for (0..6) |i| @memcpy(p[i * 4 ..][0..4], &rgba);
    c.SDL_UnmapGPUTransferBuffer(dev, tb);

    const tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_CUBE,
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER,
        .width = 1,
        .height = 1,
        .layer_count_or_depth = 6,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    for (0..6) |face| {
        c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = @intCast(face * 4), .pixels_per_row = 1, .rows_per_layer = 1 }, &c.SDL_GPUTextureRegion{ .texture = tex, .mip_level = 0, .layer = @intCast(face), .x = 0, .y = 0, .z = 0, .w = 1, .h = 1, .d = 1 }, false);
    }
    c.SDL_EndGPUCopyPass(cp);
    return tex;
}

/// Cascaded shadow map: a single `SHADOW_DIM`-wide, `SHADOW_DIM * NUM_CASCADES`-
/// tall depth atlas — one `SHADOW_DIM`² strip per cascade, stacked vertically.
/// Not a 2D array texture (SDL_GPU disallows `DEPTH_STENCIL_TARGET` usage on
/// array textures); `renderShadowPass` scissors each cascade to its own strip instead.
pub fn createShadowMap(dev: *c.SDL_GPUDevice) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.SHADOW_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = state.features.shadow_dim,
        // Height always covers the comptime maximum: the shader maps a cascade
        // to its strip as `i / NUM_CASCADES`, independent of how many are active.
        .height = state.features.shadow_dim * @as(u32, @intCast(types.NUM_CASCADES)),
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.ShadowMapCreate;
}

/// Cubemap sampler: linear filtering, clamp-to-edge. Clamping (rather than
/// `createSampler`'s repeat) avoids bleed across cube-face borders.
pub fn createCubemapSampler(dev: *c.SDL_GPUDevice) !*c.SDL_GPUSampler {
    return c.SDL_CreateGPUSampler(dev, &c.SDL_GPUSamplerCreateInfo{
        .min_filter = c.SDL_GPU_FILTER_LINEAR,
        .mag_filter = c.SDL_GPU_FILTER_LINEAR,
        .mipmap_mode = c.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR,
        .address_mode_u = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_v = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_w = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .mip_lod_bias = 0,
        .max_anisotropy = 1,
        .compare_op = c.SDL_GPU_COMPAREOP_NEVER,
        .min_lod = 0,
        .max_lod = 1000,
        .enable_anisotropy = false,
        .enable_compare = false,
        .padding1 = 0,
        .padding2 = 0,
        .props = 0,
    }) orelse error.SamplerCreate;
}

/// Create an N-mip HDR cubemap render target (sampler + color-target usage),
/// `size`x`size` per face. Used for both the equirect->cubemap conversion
/// target (1 mip) and the GGX-prefiltered specular cubemap (N mips) — see
/// `ibl_prefilter.zig`.
pub fn createCubemapTexture(dev: *c.SDL_GPUDevice, size: u32, num_levels: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_CUBE,
        .format = c.SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = size,
        .height = size,
        .layer_count_or_depth = 6,
        .num_levels = num_levels,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.TextureCreate;
}

/// Depth-comparison sampler for PCF shadow lookups (sampler2DShadow).
pub fn createShadowSampler(dev: *c.SDL_GPUDevice) !*c.SDL_GPUSampler {
    return c.SDL_CreateGPUSampler(dev, &c.SDL_GPUSamplerCreateInfo{
        .min_filter = c.SDL_GPU_FILTER_LINEAR,
        .mag_filter = c.SDL_GPU_FILTER_LINEAR,
        .mipmap_mode = c.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
        .address_mode_u = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_v = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_w = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .mip_lod_bias = 0,
        .max_anisotropy = 1,
        .compare_op = c.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
        .min_lod = 0,
        .max_lod = 1000,
        .enable_anisotropy = false,
        .enable_compare = true,
        .padding1 = 0,
        .padding2 = 0,
        .props = 0,
    }) orelse error.SamplerCreate;
}

/// Fixed-function blend equation for a material's `BlendMode`. Also reused by
/// `postprocess_pipeline.zig` (`.additive`) for the bloom upsample-combine step.
pub fn blendStateFor(mode: engine.Material.BlendMode) c.SDL_GPUColorTargetBlendState {
    return switch (mode) {
        .disabled => std.mem.zeroes(c.SDL_GPUColorTargetBlendState),
        .alpha => .{
            .src_color_blendfactor = c.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
            .dst_color_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            .color_blend_op = c.SDL_GPU_BLENDOP_ADD,
            .src_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE,
            .dst_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            .alpha_blend_op = c.SDL_GPU_BLENDOP_ADD,
            .color_write_mask = 0xf,
            .enable_blend = true,
            .enable_color_write_mask = false,
            .padding1 = 0,
            .padding2 = 0,
        },
        .additive => .{
            .src_color_blendfactor = c.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
            .dst_color_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE,
            .color_blend_op = c.SDL_GPU_BLENDOP_ADD,
            .src_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE,
            .dst_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE,
            .alpha_blend_op = c.SDL_GPU_BLENDOP_ADD,
            .color_write_mask = 0xf,
            .enable_blend = true,
            .enable_color_write_mask = false,
            .padding1 = 0,
            .padding2 = 0,
        },
    };
}

fn cullModeFor(mode: engine.Material.CullMode) c.SDL_GPUCullMode {
    return switch (mode) {
        .back => c.SDL_GPU_CULLMODE_BACK,
        .front => c.SDL_GPU_CULLMODE_FRONT,
        .none => c.SDL_GPU_CULLMODE_NONE,
    };
}

const vtx_attrs = [_]c.SDL_GPUVertexAttribute{
    .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 0, .offset = 0 },
    .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 1, .offset = 12 },
    .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, .location = 2, .offset = 24 },
};
const vtx_bufs = [_]c.SDL_GPUVertexBufferDescription{.{
    .slot = 0,
    .pitch = @sizeOf(types.GpuVertex),
    .input_rate = c.SDL_GPU_VERTEXINPUTRATE_VERTEX,
    .instance_step_rate = 0,
}};

/// Create a scene pipeline for one fixed-function state permutation (blend
/// equation, cull mode, depth write/test) — see `state.ScenePipelineState`.
pub fn createScenePipeline(dev: *c.SDL_GPUDevice, key: state.ScenePipelineState) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/scene.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/scene.frag.spv");

    const vert = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = vert_spv.len,
        .code = vert_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_VERTEX,
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.VertShader;
    defer c.SDL_ReleaseGPUShader(dev, vert);

    const frag = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = frag_spv.len,
        .code = frag_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = 10,
        .num_storage_textures = 0,
        // The scene lights storage buffer (set=2, binding=10, after the 10 samplers).
        .num_storage_buffers = 1,
        // FragUB (per-draw, slot 0) + FrameFragUB (per-pass, slot 1).
        .num_uniform_buffers = 2,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = state.HDR_COLOR_FORMAT,
        .blend_state = blendStateFor(key.blend),
    };

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 1,
        .vertex_buffer_descriptions = &vtx_bufs,
        .num_vertex_attributes = 3,
        .vertex_attributes = &vtx_attrs,
    };
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = cullModeFor(key.cull),
        // Meshes wind counter-clockwise for their outward faces.
        .front_face = c.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
        .depth_bias_constant_factor = 0,
        .depth_bias_clamp = 0,
        .depth_bias_slope_factor = 0,
        .enable_depth_bias = false,
        .enable_depth_clip = true,
        .padding1 = 0,
        .padding2 = 0,
    };
    info.depth_stencil_state = .{
        .compare_op = c.SDL_GPU_COMPAREOP_LESS,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = key.depth_test,
        .enable_depth_write = key.depth_write,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
    info.multisample_state.sample_count = state.sample_count;
    info.target_info = .{
        .num_color_targets = 1,
        .color_target_descriptions = &color_desc,
        .depth_stencil_format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

/// Depth-only pipeline for rendering the directional-light shadow map.
pub fn createShadowPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    return buildShadowPipeline(
        dev,
        @embedFile("shaders/compiled/shadow.vert.spv"),
        @embedFile("shaders/compiled/shadow.frag.spv"),
        0,
        0,
    );
}

/// Alpha-cutout variant of `createShadowPipeline`: the fragment stage samples
/// the material's albedo and discards below its cutoff, so masked geometry
/// (foliage, fences) casts the shape it actually shows rather than its quad.
pub fn createShadowMaskPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    return buildShadowPipeline(
        dev,
        @embedFile("shaders/compiled/shadow_mask.vert.spv"),
        @embedFile("shaders/compiled/shadow_mask.frag.spv"),
        1,
        1,
    );
}

/// Shared depth-only shadow pipeline setup; the variants differ only in their
/// shaders and how many fragment resources those declare.
fn buildShadowPipeline(
    dev: *c.SDL_GPUDevice,
    vert_spv: []const u8,
    frag_spv: []const u8,
    frag_samplers: u32,
    frag_uniform_buffers: u32,
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
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.VertShader;
    defer c.SDL_ReleaseGPUShader(dev, vert);

    const frag = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = frag_spv.len,
        .code = frag_spv.ptr,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = frag_samplers,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = frag_uniform_buffers,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 1,
        .vertex_buffer_descriptions = &vtx_bufs,
        .num_vertex_attributes = 3,
        .vertex_attributes = &vtx_attrs,
    };
    // Cull nothing and apply depth bias to combat shadow acne / peter-panning.
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = c.SDL_GPU_CULLMODE_NONE,
        .front_face = c.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
        .depth_bias_constant_factor = 1.25,
        .depth_bias_clamp = 0,
        .depth_bias_slope_factor = 1.75,
        .enable_depth_bias = true,
        .enable_depth_clip = true,
        .padding1 = 0,
        .padding2 = 0,
    };
    info.depth_stencil_state = .{
        .compare_op = c.SDL_GPU_COMPAREOP_LESS,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = true,
        .enable_depth_write = true,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
    info.target_info = .{
        .num_color_targets = 0,
        .color_target_descriptions = null,
        .depth_stencil_format = state.SHADOW_FORMAT,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

/// Fullscreen-triangle pipeline that renders the scene's equirect HDR
/// environment map as a background. Drawn first in the main pass (see
/// `root.zig`), before any opaque geometry, so depth test/write are disabled —
/// opaque draws simply overwrite sky pixels as they render.
pub fn createSkyboxPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/skybox.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/skybox.frag.spv");

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
        .code = frag_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = 1,
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
    info.depth_stencil_state = .{
        .compare_op = c.SDL_GPU_COMPAREOP_ALWAYS,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = false,
        .enable_depth_write = false,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
    info.multisample_state.sample_count = state.sample_count;
    info.target_info = .{
        .num_color_targets = 1,
        .color_target_descriptions = &color_desc,
        .depth_stencil_format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

/// Compute pipeline for GPU-driven frustum culling: reads a mesh's per-submesh
/// bounds storage buffer and writes one indexed indirect draw command per
/// submesh (visible or zero-instance) — see `cull.comp.glsl`.
pub fn createCullComputePipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUComputePipeline {
    const comp_spv = @embedFile("shaders/compiled/cull.comp.spv");

    return c.SDL_CreateGPUComputePipeline(dev, &c.SDL_GPUComputePipelineCreateInfo{
        .code_size = comp_spv.len,
        .code = comp_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .num_samplers = 0,
        .num_readonly_storage_textures = 0,
        .num_readonly_storage_buffers = 1,
        .num_readwrite_storage_textures = 0,
        .num_readwrite_storage_buffers = 1,
        .num_uniform_buffers = 1,
        .threadcount_x = 64,
        .threadcount_y = 1,
        .threadcount_z = 1,
        .props = 0,
    }) orelse error.ComputePipeline;
}

/// Create the offscreen depth target for the main pass at `w`×`h`, matching the
/// scene's current MSAA sample count.
pub fn createDepth(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = state.sample_count,
        .props = 0,
    }) orelse error.DepthTextureCreate;
}

/// Create the multisampled color target the scene pass renders into before
/// resolving to the caller's single-sample texture. Only used when MSAA is on.
pub fn createMsaaColor(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.HDR_COLOR_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = state.sample_count,
        .props = 0,
    }) orelse error.MsaaColorCreate;
}

/// The highest sample count (up to 4x) the device supports for both the scene
/// color and depth formats, or `SAMPLECOUNT_1` if MSAA isn't supported.
pub fn pickSampleCount(dev: *c.SDL_GPUDevice) c.SDL_GPUSampleCount {
    const color_fmt = state.HDR_COLOR_FORMAT;
    const depth_fmt = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;
    // Step the requested count down to the highest the device supports for both
    // the colour and depth formats, rather than assuming 4x is available.
    var want: u8 = state.features.msaa;
    while (want > 1) : (want /= 2) {
        const sc: c.SDL_GPUSampleCount = switch (want) {
            8 => c.SDL_GPU_SAMPLECOUNT_8,
            4 => c.SDL_GPU_SAMPLECOUNT_4,
            2 => c.SDL_GPU_SAMPLECOUNT_2,
            else => break,
        };
        if (c.SDL_GPUTextureSupportsSampleCount(dev, color_fmt, sc) and
            c.SDL_GPUTextureSupportsSampleCount(dev, depth_fmt, sc))
            return sc;
    }
    return c.SDL_GPU_SAMPLECOUNT_1;
}
