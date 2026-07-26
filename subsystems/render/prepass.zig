//! Depth+normal prepass: a single-sample, opaque-only pass that runs ahead of
//! the main forward pass so screen-space effects (SSAO now, SSR later) have
//! something to read. The main pass's own depth is never sampler-usable and
//! is MSAA when available, so it can't serve this role — see `state.PREPASS_DEPTH_FORMAT`.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const assets = @import("assets.zig");
const culling = @import("culling.zig");
const draw = @import("draw.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;
const log = std.log.scoped(.render);

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

/// Create the depth+normal prepass pipeline. Always single-sample regardless
/// of the main pass's MSAA setting.
pub fn createPrepassPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/depth_normal.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/depth_normal.frag.spv");

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
        .num_samplers = 1,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = state.PREPASS_NORMAL_FORMAT,
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
        // Cull nothing: the prepass draws every opaque material with one fixed
        // permutation, ignoring per-material cull mode. Back-face culling here
        // would punch holes in the depth buffer for double-sided materials
        // (foliage, thin fabric), which SSAO would then reproject as false contact AO.
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
    info.multisample_state.sample_count = c.SDL_GPU_SAMPLECOUNT_1;
    info.target_info = .{
        .num_color_targets = 1,
        .color_target_descriptions = &color_desc,
        .depth_stencil_format = state.PREPASS_DEPTH_FORMAT,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

fn createPrepassDepth(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.PREPASS_DEPTH_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.PrepassDepthCreate;
}

fn createPrepassNormal(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.PREPASS_NORMAL_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.PrepassNormalCreate;
}

fn makeTarget(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?state.PrepassTarget {
    const depth = createPrepassDepth(dev, w, h) catch return null;
    const normal = createPrepassNormal(dev, w, h) catch {
        c.SDL_ReleaseGPUTexture(dev, depth);
        return null;
    };
    return .{ .depth = depth, .normal = normal, .w = w, .h = h };
}

fn releaseTarget(dev: *c.SDL_GPUDevice, t: *state.PrepassTarget) void {
    if (t.depth) |d| c.SDL_ReleaseGPUTexture(dev, d);
    if (t.normal) |n| c.SDL_ReleaseGPUTexture(dev, n);
    t.* = .{};
}

fn targetFor(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?*state.PrepassTarget {
    if (state.findPrepassTarget(w, h)) |t| return t;

    for (&state.prepass_targets) |*t| {
        if (t.depth == null) {
            t.* = makeTarget(dev, w, h) orelse return null;
            return t;
        }
    }

    const t = &state.prepass_targets[state.prepass_evict_cursor];
    state.prepass_evict_cursor = (state.prepass_evict_cursor + 1) % state.prepass_targets.len;
    releaseTarget(dev, t);
    t.* = makeTarget(dev, w, h) orelse return null;
    return t;
}

/// Release every cached prepass target (called from `root.zig:deinit`).
pub fn destroyTargets(dev: *c.SDL_GPUDevice) void {
    for (&state.prepass_targets) |*t| releaseTarget(dev, t);
    state.prepass_evict_cursor = 0;
}

/// Render opaque scene depth + view-space normals into a `w`x`h` target,
/// culled against the main camera `frustum`. Draws the same geometry the main
/// pass's immediate path draws — alpha-masked materials included (with the
/// same cutout discard), alpha-blended excluded. Returns null on GPU failure.
pub fn run(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler,
    w: u32,
    h: u32,
    objects: []const engine.SceneNode,
    frustum: culling.Frustum,
    view: Matrix4,
    proj: Matrix4,
) ?*state.PrepassTarget {
    const pl = state.prepass_pipeline orelse return null;
    const white = state.white_tex orelse return null;
    const target = targetFor(dev, w, h) orelse return null;

    var zone = engine.Profiler.zone("render.prepass");
    defer zone.end();

    const vp = proj.multiply(view);

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = target.depth.?;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = target.normal.?;
    color_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;
    color_info.clear_color = .{ .r = 0.5, .g = 0.5, .b = 1.0, .a = 1.0 };

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return null;
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;
            if (gm.submeshes.len == 0) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            if (culling.aabbOutsideFrustum(gm.bounds_min, gm.bounds_max, mdl, frustum)) continue;

            const mvp = vp.multiply(mdl);
            const model_view = view.multiply(mdl);
            const vub = types.PrepassVertexUB{ .mvp = mvp.m, .model_view = model_view.m };

            c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
            c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

            const mr = &comp.mesh_renderer;
            const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);

            for (gm.submeshes) |sm| {
                if (sm.index_count == 0) continue;
                if (culling.aabbOutsideFrustum(sm.bounds_min, sm.bounds_max, mdl, frustum)) continue;

                const mat_guid = draw.materialGuidForSlot(mr, mat_n, sm.material_slot);
                const mat_res = assets.resolveMaterial(mat_guid);
                // Only the immediate-draw (opaque) path contributes to AO —
                // matches root.zig's `blend == .disabled` split exactly.
                if (mat_res.render.blend != .disabled) continue;

                const albedo_t = assets.pickTexture(mat_res.map(.albedo), white);
                const fub = types.PrepassFragUB{
                    .flags = .{ assets.present(albedo_t.found), mat_res.alpha_cutoff, assets.present(mat_res.render.alpha_mask), 0 },
                    .base_color = mat_res.base_color,
                };

                c.SDL_PushGPUVertexUniformData(cmd, 0, &vub, @sizeOf(types.PrepassVertexUB));
                c.SDL_PushGPUFragmentUniformData(cmd, 0, &fub, @sizeOf(types.PrepassFragUB));
                c.SDL_BindGPUFragmentSamplers(pass, 0, &[_]c.SDL_GPUTextureSamplerBinding{
                    .{ .texture = albedo_t.tex, .sampler = sampler },
                }, 1);
                c.SDL_DrawGPUIndexedPrimitives(pass, sm.index_count, 1, sm.index_offset, 0, 0);
            }
        }
    }

    c.SDL_EndGPURenderPass(pass);
    return target;
}
