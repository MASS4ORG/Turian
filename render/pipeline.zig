//! GPU pipeline, sampler, and helper-texture creation (SDL3 GPU + SPIR-V).
const std = @import("std");
const gpu = @import("gpu");
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

pub fn createShadowMap(dev: *c.SDL_GPUDevice) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.SHADOW_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = types.SHADOW_DIM,
        .height = types.SHADOW_DIM,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.ShadowMapCreate;
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

pub fn createPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
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
        .num_samplers = 6,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .blend_state = std.mem.zeroes(c.SDL_GPUColorTargetBlendState),
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
        .cull_mode = c.SDL_GPU_CULLMODE_BACK,
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
        .enable_depth_test = true,
        .enable_depth_write = true,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
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
    const vert_spv = @embedFile("shaders/compiled/shadow.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/shadow.frag.spv");

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
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 0,
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

/// Create the offscreen depth target for the main pass at `w`×`h`.
pub fn createDepth(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.DepthTextureCreate;
}
