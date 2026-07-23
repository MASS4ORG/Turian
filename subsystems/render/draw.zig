//! Draw-call submission: the scene pipeline cache, per-draw uniform/sampler
//! binding, and the deferred transparent-draw scratch buffer. Shared by
//! `root.zig`'s per-submesh (CPU-culled) and per-material-group (GPU-driven
//! indirect) draw paths.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const assets = @import("assets.zig");
const pipeline = @import("pipeline.zig");

const c = gpu.c;
const log = std.log.scoped(.render);

/// The scene pipeline matching `key`, creating and caching it on first use.
/// Returns the cache's first entry (built lazily, so only once real draws start)
/// if the cache is full — better a wrong blend/cull than a dropped draw.
pub fn scenePipelineFor(dev: *c.SDL_GPUDevice, key: state.ScenePipelineState) ?*c.SDL_GPUGraphicsPipeline {
    for (state.scene_pipelines[0..state.scene_pipeline_count]) |*e| {
        if (std.meta.eql(e.key, key)) return e.pipeline;
    }
    if (state.scene_pipeline_count >= state.MAX_SCENE_PIPELINES) {
        log.warn("scene pipeline cache full ({d}) — reusing an existing pipeline for {any}", .{ state.MAX_SCENE_PIPELINES, key });
        return if (state.scene_pipeline_count > 0) state.scene_pipelines[0].pipeline else null;
    }
    const p = pipeline.createScenePipeline(dev, key) catch |err| {
        log.err("scene pipeline create failed: {any}", .{err});
        return null;
    };
    state.scene_pipelines[state.scene_pipeline_count] = .{ .key = key, .pipeline = p };
    state.scene_pipeline_count += 1;
    return p;
}

pub fn destroyScenePipelines(dev: *c.SDL_GPUDevice) void {
    for (state.scene_pipelines[0..state.scene_pipeline_count]) |*e| {
        if (e.pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    }
    state.scene_pipeline_count = 0;
}

/// Per-frame values a draw's fragment uniforms need but that don't vary
/// per-draw (lighting is scene-wide, not per-material).
pub const FrameUniforms = struct {
    cam_pos4: [4]f32,
    light_vp: [16]f32,
    lights: [types.MAX_LIGHTS]types.GpuLight,
    env_params: [4]f32,
    env_sh: [9][4]f32,
};

/// Everything one submesh draw needs: which pipeline permutation, geometry
/// range, and the material's uniform/sampler values.
pub const DrawParams = struct {
    pl_key: state.ScenePipelineState,
    vtx_buf: *c.SDL_GPUBuffer,
    idx_buf: *c.SDL_GPUBuffer,
    index_offset: u32,
    index_count: u32,
    vub: types.VertexUB,
    base_color: [4]f32,
    mr_ns_oc: [4]f32,
    emissive: [4]f32,
    flags: [4]f32,
    flags2: [4]f32,
    bindings: [7]c.SDL_GPUTextureSamplerBinding,
};

/// A blended/additive draw deferred for back-to-front sorting.
pub const TransparentDraw = struct {
    params: DrawParams,
    /// Squared camera distance to the drawing node's origin — cheap proxy for
    /// per-node (not per-triangle) back-to-front ordering.
    sort_depth: f32,
};

/// Static scratch space for one frame's deferred transparent draws.
pub const MAX_TRANSPARENT_DRAWS = 1024;
pub var transparent_draws: [MAX_TRANSPARENT_DRAWS]TransparentDraw = undefined;
pub var transparent_count: usize = 0;

pub fn transparentFartherFirst(_: void, a: TransparentDraw, b: TransparentDraw) bool {
    return a.sort_depth > b.sort_depth;
}

/// Bind a draw's pipeline (if it isn't already bound) and push its uniforms
/// and texture bindings — everything `submitDraw`/`submitIndirectDraw` share,
/// short of the final draw call itself.
fn bindDrawState(
    cmd: *c.SDL_GPUCommandBuffer,
    pass: *c.SDL_GPURenderPass,
    dev: *c.SDL_GPUDevice,
    bound_pipeline: *?*c.SDL_GPUGraphicsPipeline,
    fu: FrameUniforms,
    dp: DrawParams,
) void {
    const pl = scenePipelineFor(dev, dp.pl_key) orelse return;
    if (bound_pipeline.* != pl) {
        c.SDL_BindGPUGraphicsPipeline(pass, pl);
        bound_pipeline.* = pl;
    }

    c.SDL_PushGPUVertexUniformData(cmd, 0, &dp.vub, @sizeOf(types.VertexUB));
    c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = dp.vtx_buf, .offset = 0 }, 1);
    c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = dp.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

    const fub = types.FragUB{
        .camera_pos = fu.cam_pos4,
        .base_color = dp.base_color,
        .mr_ns_oc = dp.mr_ns_oc,
        .emissive = dp.emissive,
        .flags = dp.flags,
        .flags2 = dp.flags2,
        .env_params = fu.env_params,
        .env_sh = fu.env_sh,
        .light_vp = fu.light_vp,
        .lights = fu.lights,
    };
    c.SDL_PushGPUFragmentUniformData(cmd, 0, &fub, @sizeOf(types.FragUB));
    c.SDL_BindGPUFragmentSamplers(pass, 0, &dp.bindings, 7);
}

/// Bind the draw's state and issue one indexed draw call for a single submesh.
pub fn submitDraw(
    cmd: *c.SDL_GPUCommandBuffer,
    pass: *c.SDL_GPURenderPass,
    dev: *c.SDL_GPUDevice,
    bound_pipeline: *?*c.SDL_GPUGraphicsPipeline,
    fu: FrameUniforms,
    dp: DrawParams,
) void {
    bindDrawState(cmd, pass, dev, bound_pipeline, fu, dp);
    c.SDL_DrawGPUIndexedPrimitives(pass, dp.index_count, 1, dp.index_offset, 0, 0);
    // Every mesh binds samplers.
    engine.Profiler.countDraw(dp.index_count / 3, dp.index_count, true);
}

/// Bind one material group and issue an indirect multi-draw call into `indirect_buf`.
pub fn submitIndirectDraw(
    cmd: *c.SDL_GPUCommandBuffer,
    pass: *c.SDL_GPURenderPass,
    dev: *c.SDL_GPUDevice,
    bound_pipeline: *?*c.SDL_GPUGraphicsPipeline,
    fu: FrameUniforms,
    dp: DrawParams,
    indirect_buf: *c.SDL_GPUBuffer,
    byte_offset: u32,
    draw_count: u32,
) void {
    bindDrawState(cmd, pass, dev, bound_pipeline, fu, dp);
    c.SDL_DrawGPUIndexedPrimitivesIndirect(pass, indirect_buf, byte_offset, draw_count);
    engine.Profiler.countDraw(draw_count, draw_count, true);
    engine.Profiler.countSubmeshesDrawn(draw_count);
}

/// Textures/samplers a draw's fragment uniforms need that don't vary by
/// submesh or material group within one `renderScene` call.
pub const DrawCtx = struct {
    shadow_tex: *c.SDL_GPUTexture,
    shadow_smp: *c.SDL_GPUSampler,
    env_gpu_tex: *c.SDL_GPUTexture,
    white: *c.SDL_GPUTexture,
    flat_n: *c.SDL_GPUTexture,
    sampler: *c.SDL_GPUSampler,
};

/// Material-slot GUID, or empty for out-of-range/absent slots.
pub fn materialGuidForSlot(mr: *const engine.MeshRendererComponent, mat_n: u32, slot: i32) []const u8 {
    return if (slot >= 0 and @as(u32, @intCast(slot)) < mat_n)
        mr.materials[@intCast(slot)].slice()
    else
        "";
}

/// Flatten a material into `DrawParams` for either the CPU or indirect draw path.
pub fn buildDrawParams(
    mat_res: *const types.ResolvedMaterial,
    gm: *const state.GpuMesh,
    index_offset: u32,
    index_count: u32,
    vub: types.VertexUB,
    receives: bool,
    ctx: DrawCtx,
) DrawParams {
    const albedo_t = assets.pickTexture(mat_res.map(.albedo), ctx.white);
    const mr_t = assets.pickTexture(mat_res.map(.mr), ctx.white);
    const normal_t = assets.pickTexture(mat_res.map(.normal), ctx.flat_n);
    const emis_t = assets.pickTexture(mat_res.map(.emissive), ctx.white);
    const occ_t = assets.pickTexture(mat_res.map(.occlusion), ctx.white);
    return .{
        .pl_key = .{
            .blend = mat_res.render.blend,
            .cull = mat_res.render.cull,
            .depth_write = mat_res.render.depth_write,
            .depth_test = mat_res.render.depth_test,
        },
        .vtx_buf = gm.vtx_buf,
        .idx_buf = gm.idx_buf,
        .index_offset = index_offset,
        .index_count = index_count,
        .vub = vub,
        .base_color = mat_res.base_color,
        .mr_ns_oc = .{ mat_res.metallic, mat_res.roughness, mat_res.normal_scale, mat_res.occlusion_strength },
        .emissive = .{ mat_res.emissive[0], mat_res.emissive[1], mat_res.emissive[2], mat_res.emissive_strength },
        .flags = .{ assets.present(albedo_t.found), assets.present(mr_t.found), assets.present(normal_t.found), assets.present(emis_t.found) },
        .flags2 = .{ assets.present(occ_t.found), mat_res.alpha_cutoff, assets.present(mat_res.render.alpha_mask), assets.present(receives) },
        .bindings = .{
            .{ .texture = albedo_t.tex, .sampler = ctx.sampler },
            .{ .texture = mr_t.tex, .sampler = ctx.sampler },
            .{ .texture = normal_t.tex, .sampler = ctx.sampler },
            .{ .texture = emis_t.tex, .sampler = ctx.sampler },
            .{ .texture = occ_t.tex, .sampler = ctx.sampler },
            .{ .texture = ctx.shadow_tex, .sampler = ctx.shadow_smp },
            .{ .texture = ctx.env_gpu_tex, .sampler = ctx.sampler },
        },
    };
}
