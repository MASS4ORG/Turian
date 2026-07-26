//! Directional-light shadow mapping: cascaded frustum fit + the depth-only pass.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const assets = @import("assets.zig");
const culling = @import("culling.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

pub const NUM_CASCADES = types.NUM_CASCADES;

pub const Bounds = struct { center: Vector3, radius: f32 };

/// One cascade's fitted light frustum: the view-projection used both to
/// render its shadow-map layer and to sample it back in `scene.frag.glsl`.
pub const Cascade = struct {
    vp: Matrix4,
    /// Far distance of this cascade along the camera's forward axis (world
    /// units from the eye) — the shader picks a cascade per-pixel against this.
    far_distance: f32,
    /// 1/(ortho far-near) for this cascade's frustum, converting a world-unit
    /// shadow bias into the [0,1] NDC depth range sampled against the map.
    depth_scale: f32,
};

/// Axis-aligned bounds of every shadow-casting mesh in the scene, used to
/// clamp how far the cascades need to reach. Unions the meshes' real
/// world-space AABBs, since sizing from object transforms instead would
/// collapse the frustum for a scene authored as one large mesh at the origin.
pub fn sceneBounds(objects: []const engine.SceneNode) Bounds {
    var min = Vector3{ .x = 1e30, .y = 1e30, .z = 1e30 };
    var max = Vector3{ .x = -1e30, .y = -1e30, .z = -1e30 };
    var any = false;
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            if (!comp.mesh_renderer.cast_shadows) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            const box = culling.worldAabb(gm.bounds_min, gm.bounds_max, mdl);

            min = .{
                .x = @min(min.x, box.center.x - box.extent.x),
                .y = @min(min.y, box.center.y - box.extent.y),
                .z = @min(min.z, box.center.z - box.extent.z),
            };
            max = .{
                .x = @max(max.x, box.center.x + box.extent.x),
                .y = @max(max.y, box.center.y + box.extent.y),
                .z = @max(max.z, box.center.z + box.extent.z),
            };
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

/// Practical split scheme (Zhang et al.): blends logarithmic and uniform
/// splits so near cascades stay tight (log) while far ones don't shrink to
/// nothing (uniform). `lambda = 0.5` is the usual middle ground.
fn splitDistances(near: f32, far: f32) [NUM_CASCADES]f32 {
    const lambda: f32 = 0.5;
    var splits: [NUM_CASCADES]f32 = undefined;
    for (0..NUM_CASCADES) |i| {
        const p = @as(f32, @floatFromInt(i + 1)) / @as(f32, @floatFromInt(NUM_CASCADES));
        const log_split = near * std.math.pow(f32, far / near, p);
        const uniform_split = near + (far - near) * p;
        splits[i] = lambda * log_split + (1.0 - lambda) * uniform_split;
    }
    return splits;
}

/// The 8 world-space corners of the camera's view frustum between view-space
/// distances `near_d` and `far_d`, measured from the eye along the view axis.
/// `inv_view` is the camera's world transform (inverse of its view matrix).
/// View space follows `lookAt`'s "+Z forward" convention, so a point `d` units ahead sits at z = -d.
fn frustumCorners(inv_view: Matrix4, fov_deg: f32, aspect: f32, near_d: f32, far_d: f32) [8]Vector3 {
    const tan_half_fovy = @tan(fov_deg * std.math.pi / 360.0);
    var corners: [8]Vector3 = undefined;
    var i: usize = 0;
    for ([_]f32{ near_d, far_d }) |d| {
        const hh = d * tan_half_fovy;
        const hw = hh * aspect;
        for ([_]f32{ -1.0, 1.0 }) |sx| {
            for ([_]f32{ -1.0, 1.0 }) |sy| {
                corners[i] = inv_view.transformPoint(.{ .x = sx * hw, .y = sy * hh, .z = -d });
                i += 1;
            }
        }
    }
    return corners;
}

const FittedCascade = struct { vp: Matrix4, depth_scale: f32 };

/// Fits one cascade's orthographic light frustum to `corners` (that cascade's
/// camera sub-frustum corners, in world space). Uses a bounding sphere, not a
/// tight AABB, so frustum size depends only on the sub-frustum's shape, not
/// camera yaw — an AABB would resize and visibly shimmer as the camera turns.
fn fitCascade(corners: [8]Vector3, dir: Vector3, up: Vector3, view0: Matrix4, inv_view0: Matrix4) FittedCascade {
    var center = Vector3{ .x = 0, .y = 0, .z = 0 };
    for (corners) |p| {
        center.x += p.x;
        center.y += p.y;
        center.z += p.z;
    }
    center.x /= 8.0;
    center.y /= 8.0;
    center.z /= 8.0;

    var radius: f32 = 1.0;
    for (corners) |p| {
        const dx = p.x - center.x;
        const dy = p.y - center.y;
        const dz = p.z - center.z;
        radius = @max(radius, @sqrt(dx * dx + dy * dy + dz * dz));
    }

    // Snap the frustum center to whole texels in `view0`'s light-space basis
    // (rotation fixed by `dir` alone), or the map's footprint slides by
    // sub-texel amounts each frame and shadow edges crawl.
    const texel = (2.0 * radius) / @as(f32, @floatFromInt(types.SHADOW_DIM));
    const center_ls = view0.transformPoint(center);
    const snapped_ls = Vector3{
        .x = @round(center_ls.x / texel) * texel,
        .y = @round(center_ls.y / texel) * texel,
        .z = center_ls.z,
    };
    const snapped_center = inv_view0.transformPoint(snapped_ls);

    const eye = Vector3{
        .x = snapped_center.x - dir.x * radius * 2.0,
        .y = snapped_center.y - dir.y * radius * 2.0,
        .z = snapped_center.z - dir.z * radius * 2.0,
    };
    const view = Matrix4.lookAt(eye, snapped_center, up);
    const near_p: f32 = 0.01;
    const far_p = radius * 4.0 + 1.0;
    const ortho = Matrix4.orthographic(-radius, radius, -radius, radius, near_p, far_p);
    return .{ .vp = ortho.multiply(view), .depth_scale = 1.0 / (far_p - near_p) };
}

/// Computes the `NUM_CASCADES` shadow frustums covering the camera's view out
/// to `min(cam_far, bounds diameter)` — clamping the far cascade to the
/// scene's actual extent keeps distant, empty sky from stealing shadow-map
/// resolution the geometry could otherwise use.
pub fn computeCascades(
    cam_view: Matrix4,
    fov_deg: f32,
    aspect: f32,
    cam_near: f32,
    cam_far: f32,
    dir: Vector3,
    bounds: Bounds,
) [NUM_CASCADES]Cascade {
    const dl = @sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
    const d = if (dl > 1e-6) Vector3{ .x = dir.x / dl, .y = dir.y / dl, .z = dir.z / dl } else Vector3{ .x = 0, .y = -1, .z = 0 };
    const up = if (@abs(d.y) > 0.99) Vector3{ .x = 0, .y = 0, .z = 1 } else Vector3{ .x = 0, .y = 1, .z = 0 };

    const view0 = Matrix4.lookAt(.{ .x = 0, .y = 0, .z = 0 }, d, up);
    const inv_view0 = view0.inverse();

    const shadow_far = @max(@min(cam_far, bounds.radius * 2.0), cam_near + 1.0);
    const splits = splitDistances(cam_near, shadow_far);
    const inv_view = cam_view.inverse();

    var out: [NUM_CASCADES]Cascade = undefined;
    var near_d = cam_near;
    for (0..NUM_CASCADES) |i| {
        const far_d = splits[i];
        const corners = frustumCorners(inv_view, fov_deg, aspect, near_d, far_d);
        const fitted = fitCascade(corners, d, up, view0, inv_view0);
        out[i] = .{ .vp = fitted.vp, .far_distance = far_d, .depth_scale = fitted.depth_scale };
        near_d = far_d;
    }
    return out;
}

/// Render scene depth from the light's point of view into each cascade's
/// strip of the shadow atlas (see `pipeline.createShadowMap`): one render
/// pass over the whole atlas, each cascade's draws restricted to its own
/// viewport/scissor strip and its own GPU-culled indirect buffer (see `gpu_cull.dispatchShadowCulls`).
pub fn renderShadowPass(cmd: *c.SDL_GPUCommandBuffer, cascades: [NUM_CASCADES]Cascade, objects: []const engine.SceneNode) void {
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

    for (cascades, 0..) |cascade, strip| {
        const y_off: i32 = @intCast(strip * @as(usize, types.SHADOW_DIM));
        c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
            .x = 0,
            .y = @floatFromInt(y_off),
            .w = @floatFromInt(types.SHADOW_DIM),
            .h = @floatFromInt(types.SHADOW_DIM),
            .min_depth = 0.0,
            .max_depth = 1.0,
        });
        c.SDL_SetGPUScissor(pass, &c.SDL_Rect{
            .x = 0,
            .y = y_off,
            .w = @intCast(types.SHADOW_DIM),
            .h = @intCast(types.SHADOW_DIM),
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
                const lub = types.ShadowUB{ .light_mvp = cascade.vp.multiply(mdl).m };
                c.SDL_PushGPUVertexUniformData(cmd, 0, &lub, @sizeOf(types.ShadowUB));

                c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
                c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

                // Draw only the submeshes this cascade's own cull marked visible
                // (one indirect multi-draw); fall back to the whole mesh if that
                // cull didn't run for this mesh (e.g. its compute buffers failed
                // to create).
                if (gm.shadow_indirect_bufs[strip]) |sib| {
                    if (gm.shadow_cull_dispatched_frame[strip] == state.frame_seq) {
                        c.SDL_DrawGPUIndexedPrimitivesIndirect(pass, sib, 0, @intCast(gm.submeshes.len));
                        continue;
                    }
                }
                c.SDL_DrawGPUIndexedPrimitives(pass, gm.idx_count, 1, 0, 0, 0);
            }
        }
    }
}
