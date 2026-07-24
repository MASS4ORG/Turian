//! GPU-driven frustum culling: a compute pass that writes per-submesh indirect
//! draw commands, then the render pass issues one `DrawIndirect` per material group.
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const culling = @import("culling.zig");
const assets = @import("assets.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;

/// Dispatches the cull compute pass for `gm`, writing `gm.indirect_buf` in place
/// (camera-frustum visibility for the main pass).
pub fn dispatchCull(cmd: *c.SDL_GPUCommandBuffer, gm: *const state.GpuMesh, model: Matrix4, frustum: culling.Frustum) void {
    const indirect_buf = gm.indirect_buf orelse return;
    dispatchCullTo(cmd, gm, model, frustum, indirect_buf);
}

/// One dispatch per shadow-casting mesh (first instance this frame), culling
/// against the light frustum into `shadow_indirect_buf` for the shadow pass.
pub fn dispatchShadowCulls(cmd: *c.SDL_GPUCommandBuffer, objects: []const engine.SceneNode, frustum: culling.Frustum) void {
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            if (!comp.mesh_renderer.cast_shadows) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;
            const shadow_buf = gm.shadow_indirect_buf orelse continue;
            if (gm.submeshes.len == 0) continue;
            if (gm.shadow_cull_dispatched_frame == state.frame_seq) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            // Always dispatch: a mesh wholly outside the light frustum has every
            // submesh marked zero-instance by the compute pass (a no-op indirect
            // draw), which is exactly the wanted cull — cleaner than skipping the
            // dispatch, which would leave the pass falling back to a full draw.
            dispatchCullTo(cmd, gm, mdl, frustum, shadow_buf);
            gm.shadow_cull_dispatched_frame = state.frame_seq;
        }
    }
}

/// Dispatches the cull compute pass for `gm` against `frustum`, writing indirect
/// draw commands into `out_buf`.
pub fn dispatchCullTo(cmd: *c.SDL_GPUCommandBuffer, gm: *const state.GpuMesh, model: Matrix4, frustum: culling.Frustum, out_buf: *c.SDL_GPUBuffer) void {
    const cull_pl = state.cull_pipeline orelse return;
    const bounds_buf = gm.bounds_buf orelse return;
    const indirect_buf = out_buf;
    if (gm.submeshes.len == 0) return;

    var planes: [6][4]f32 = undefined;
    for (frustum.planes, 0..) |p, i| planes[i] = .{ p.a, p.b, p.c, p.d };
    const ub = types.CullUB{
        .model = model.m,
        .planes = planes,
        .submesh_count = .{ @intCast(gm.submeshes.len), 0, 0, 0 },
    };

    const cpass = c.SDL_BeginGPUComputePass(
        cmd,
        null,
        0,
        &[_]c.SDL_GPUStorageBufferReadWriteBinding{.{ .buffer = indirect_buf, .cycle = false, .padding1 = 0, .padding2 = 0, .padding3 = 0 }},
        1,
    ) orelse return;
    c.SDL_BindGPUComputePipeline(cpass, cull_pl);
    c.SDL_BindGPUComputeStorageBuffers(cpass, 0, &[_]?*c.SDL_GPUBuffer{bounds_buf}, 1);
    c.SDL_PushGPUComputeUniformData(cmd, 0, &ub, @sizeOf(types.CullUB));
    const groups: u32 = @intCast((gm.submeshes.len + 63) / 64);
    c.SDL_DispatchGPUCompute(cpass, groups, 1, 1);
    c.SDL_EndGPUComputePass(cpass);
}
