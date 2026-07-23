//! Opt-in fence-bracketed per-pass GPU timing. Off by default — forces a
//! pipeline stall between the shadow and cull compute passes.
const engine = @import("engine");
const gpu = @import("gpu");
const state = @import("state.zig");
const shadow = @import("shadow.zig");
const gpu_cull = @import("gpu_cull.zig");
const assets = @import("assets.zig");
const culling = @import("culling.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;

/// Runs the shadow pass; fence-waits its own command buffer in detailed mode.
pub fn runShadowPass(dev: *c.SDL_GPUDevice, cmd: *c.SDL_GPUCommandBuffer, light_vp: Matrix4, objects: []const engine.SceneNode) void {
    if (!state.detailed_gpu_timing) {
        var z = engine.Profiler.zone("render.shadow");
        defer z.end();
        shadow.renderShadowPass(cmd, light_vp, objects);
        return;
    }
    const own = c.SDL_AcquireGPUCommandBuffer(dev) orelse {
        var z = engine.Profiler.zone("render.shadow");
        defer z.end();
        shadow.renderShadowPass(cmd, light_vp, objects);
        return;
    };
    {
        var z = engine.Profiler.zone("render.shadow");
        defer z.end();
        shadow.renderShadowPass(own, light_vp, objects);
    }
    var gz = engine.Profiler.zone("gpu.shadow");
    defer gz.end();
    gpu.submitAndWait(dev, own);
}

/// Runs the GPU-driven cull compute phase; fence-waits its own command buffer in detailed mode.
pub fn runCullPhase(dev: *c.SDL_GPUDevice, cmd: *c.SDL_GPUCommandBuffer, objects: []const engine.SceneNode, frustum: culling.Frustum) void {
    if (!state.detailed_gpu_timing) {
        var z = engine.Profiler.zone("render.cull");
        defer z.end();
        dispatchAll(cmd, objects, frustum);
        return;
    }
    const own = c.SDL_AcquireGPUCommandBuffer(dev) orelse {
        var z = engine.Profiler.zone("render.cull");
        defer z.end();
        dispatchAll(cmd, objects, frustum);
        return;
    };
    {
        var z = engine.Profiler.zone("render.cull");
        defer z.end();
        dispatchAll(own, objects, frustum);
    }
    var gz = engine.Profiler.zone("gpu.cull");
    defer gz.end();
    gpu.submitAndWait(dev, own);
}

/// One dispatch per mesh renderer not yet culled this frame.
fn dispatchAll(cmd: *c.SDL_GPUCommandBuffer, objects: []const engine.SceneNode, frustum: culling.Frustum) void {
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;
            if (gm.submeshes.len == 0 or gm.indirect_buf == null) continue;
            if (gm.cull_dispatched_frame == state.frame_seq) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            if (culling.aabbOutsideFrustum(gm.bounds_min, gm.bounds_max, mdl, frustum)) continue;

            gpu_cull.dispatchCull(cmd, gm, mdl, frustum);
            gm.cull_dispatched_frame = state.frame_seq;
        }
    }
}
