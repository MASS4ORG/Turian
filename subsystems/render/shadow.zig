//! Directional-light shadow mapping: frustum fit + the depth-only pass.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const assets = @import("assets.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

pub const Bounds = struct { center: Vector3, radius: f32 };

/// Axis-aligned bounds of all active mesh-renderer object positions, with a
/// margin for mesh extent. Used to fit the directional shadow frustum.
pub fn sceneBounds(objects: []const engine.SceneNode) Bounds {
    var min = Vector3{ .x = 1e30, .y = 1e30, .z = 1e30 };
    var max = Vector3{ .x = -1e30, .y = -1e30, .z = -1e30 };
    var any = false;
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const p = obj.transform.position;
            // Approximate mesh half-extent from the object scale.
            const t = &obj.transform;
            const e = @max(@abs(t.scale.x), @max(@abs(t.scale.y), @abs(t.scale.z)));
            min = .{ .x = @min(min.x, p.x - e), .y = @min(min.y, p.y - e), .z = @min(min.z, p.z - e) };
            max = .{ .x = @max(max.x, p.x + e), .y = @max(max.y, p.y + e), .z = @max(max.z, p.z + e) };
            any = true;
        }
    }
    if (!any) return .{ .center = .{ .x = 0, .y = 0, .z = 0 }, .radius = 10 };
    const center = Vector3{ .x = (min.x + max.x) * 0.5, .y = (min.y + max.y) * 0.5, .z = (min.z + max.z) * 0.5 };
    const dx = max.x - center.x;
    const dy = max.y - center.y;
    const dz = max.z - center.z;
    const radius = @max(1.0, @sqrt(dx * dx + dy * dy + dz * dz));
    return .{ .center = center, .radius = radius };
}

/// Build the orthographic light view-projection covering `bounds`, looking
/// along `dir` (the directional light's travel direction).
pub fn shadowMatrix(dir: Vector3, bounds: Bounds) Matrix4 {
    const dl = @sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
    const d = if (dl > 1e-6) Vector3{ .x = dir.x / dl, .y = dir.y / dl, .z = dir.z / dl } else Vector3{ .x = 0, .y = -1, .z = 0 };
    const r = bounds.radius;
    const eye = Vector3{ .x = bounds.center.x - d.x * r * 2.0, .y = bounds.center.y - d.y * r * 2.0, .z = bounds.center.z - d.z * r * 2.0 };
    // Pick an up vector not parallel to the light direction.
    const up = if (@abs(d.y) > 0.99) Vector3{ .x = 0, .y = 0, .z = 1 } else Vector3{ .x = 0, .y = 1, .z = 0 };
    const view = Matrix4.lookAt(eye, bounds.center, up);
    const ortho = Matrix4.orthographic(-r, r, -r, r, 0.01, r * 4.0 + 1.0);
    return ortho.multiply(view);
}

/// Render scene depth from the light's point of view into the shadow map.
pub fn renderShadowPass(cmd: *c.SDL_GPUCommandBuffer, light_vp: Matrix4, objects: []const engine.SceneNode) void {
    const shadow_map = state.shadow_map orelse return;
    const pipeline = state.shadow_pipeline orelse return;

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = shadow_map;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, null, 0, &depth_info) orelse return;
    defer c.SDL_EndGPURenderPass(pass);
    c.SDL_BindGPUGraphicsPipeline(pass, pipeline);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(types.SHADOW_DIM),
        .h = @floatFromInt(types.SHADOW_DIM),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            if (!comp.mesh_renderer.cast_shadows) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;
            if (gm.idx_count == 0) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            const lub = types.ShadowUB{ .light_mvp = light_vp.multiply(mdl).m };
            c.SDL_PushGPUVertexUniformData(cmd, 0, &lub, @sizeOf(types.ShadowUB));

            c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
            c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

            // Draw only the submeshes the light-frustum cull marked visible (one
            // indirect multi-draw); fall back to the whole mesh if that cull
            // didn't run for this mesh (e.g. its compute buffers failed to create).
            if (gm.shadow_indirect_buf) |sib| {
                if (gm.shadow_cull_dispatched_frame == state.frame_seq) {
                    c.SDL_DrawGPUIndexedPrimitivesIndirect(pass, sib, 0, @intCast(gm.submeshes.len));
                    continue;
                }
            }
            c.SDL_DrawGPUIndexedPrimitives(pass, gm.idx_count, 1, 0, 0, 0);
        }
    }
}
