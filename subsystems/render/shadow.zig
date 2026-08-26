//! Directional-light shadow mapping: cascaded frustum fit + the depth-only pass.
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
const Vector3 = engine.Vector3;

pub const NUM_CASCADES = types.NUM_CASCADES;

/// Cascades actually fitted and rendered this frame, clamped to the comptime
/// maximum the uniform arrays are sized for.
pub fn activeCascades() usize {
    return @max(1, @min(state.features.cascade_count, NUM_CASCADES));
}

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

/// Floor for the near distance the split scheme is given. A camera near plane
/// of a centimetre or two makes `(far/near)^p` explode, which drives every
/// logarithmic split below the first metre and leaves the uniform term to pick
/// the splits on its own — so the near cascades end up covering tens of metres.
const SPLIT_NEAR_MIN: f32 = 0.5;

/// Practical split scheme (Zhang et al.): blends logarithmic and uniform
/// splits so near cascades stay tight (log) while far ones don't shrink to
/// nothing (uniform). Weighted toward the logarithmic term, which is what keeps
/// the first cascade small enough to spend its texels on near-field detail.
fn splitDistances(near: f32, far: f32, count: usize) [NUM_CASCADES]f32 {
    const lambda: f32 = 0.85;
    var splits: [NUM_CASCADES]f32 = .{far} ** NUM_CASCADES;
    for (0..count) |i| {
        const p = @as(f32, @floatFromInt(i + 1)) / @as(f32, @floatFromInt(count));
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

/// Depth of `p` along the light axis measured from `eye` — the coordinate the
/// fitted orthographic frustum's near and far planes are expressed in.
fn depthAlong(p: Vector3, eye: Vector3, dir: Vector3) f32 {
    return (p.x - eye.x) * dir.x + (p.y - eye.y) * dir.y + (p.z - eye.z) * dir.z;
}

/// Fits one cascade's orthographic light frustum to `corners` (that cascade's
/// camera sub-frustum corners, in world space). Uses a bounding sphere, not a
/// tight AABB, so frustum size depends only on the sub-frustum's shape, not
/// camera yaw — an AABB would resize and visibly shimmer as the camera turns.
fn fitCascade(corners: [8]Vector3, dir: Vector3, up: Vector3, view0: Matrix4, inv_view0: Matrix4, bounds: Bounds) FittedCascade {
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
    // Occluders standing between the light and this cascade still have to reach
    // the depth pass, so the near plane retreats far enough to clear the whole
    // scene along the light axis instead of sitting just in front of the eye. A
    // fixed near clips a caster taller than the cascade's own sphere — and culls
    // it too, since `gpu_cull` takes its planes from this same matrix. An
    // orthographic frustum accepts the resulting negative near.
    const near_p = @min(0.01, depthAlong(bounds.center, eye, dir) - bounds.radius);
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

    const active = activeCascades();
    const shadow_far = @max(@min(cam_far, bounds.radius * 2.0), cam_near + 1.0);
    const splits = splitDistances(@max(cam_near, SPLIT_NEAR_MIN), shadow_far, active);
    const inv_view = cam_view.inverse();

    var out: [NUM_CASCADES]Cascade = std.mem.zeroes([NUM_CASCADES]Cascade);
    var near_d = cam_near;
    for (0..active) |i| {
        const far_d = splits[i];
        const corners = frustumCorners(inv_view, fov_deg, aspect, near_d, far_d);
        const fitted = fitCascade(corners, d, up, view0, inv_view0, bounds);
        out[i] = .{ .vp = fitted.vp, .far_distance = far_d, .depth_scale = fitted.depth_scale };
        near_d = far_d;
    }
    return out;
}

const Occlusion = state.ShadowOcclusion;

/// Blended surfaces (glass, water) transmit light, so they occlude nothing —
/// casting a solid shadow from a shopfront window would black out the interior
/// behind it. Masked surfaces occlude only where the cutout test keeps them.
fn occlusionFor(render: engine.Material.RenderState) Occlusion {
    if (render.blend != .disabled) return .none;
    return if (render.alpha_mask) .cutout else .solid;
}

/// `gm`'s per-group shadow state, resolving it if this frame hasn't yet for
/// this renderer. Cascades run back to back over the same meshes, so without
/// this the same materials resolve once per cascade.
fn shadowGroupsFor(gm: *state.GpuMesh, mr: *const engine.MeshRendererComponent) []const state.ShadowGroup {
    if (gm.shadow_groups.len != gm.material_groups.len) return &.{};
    if (gm.shadow_groups_frame == state.frame_seq and gm.shadow_groups_owner == mr)
        return gm.shadow_groups;

    const white = state.white_tex;
    const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);
    for (gm.material_groups, gm.shadow_groups) |group, *out| {
        const mat = assets.resolveMaterial(draw.materialGuidForSlot(mr, mat_n, group.material_slot));
        const occ = occlusionFor(mat.render);
        out.* = .{ .occlusion = occ };
        if (occ != .cutout) continue;
        const albedo = assets.pickTexture(mat.map(.albedo), white orelse continue);
        out.fub = .{
            .flags = .{ assets.present(albedo.found), mat.alpha_cutoff, 0, 0 },
            .base_color = mat.base_color,
        };
        out.albedo = albedo.tex;
    }
    gm.shadow_groups_frame = state.frame_seq;
    gm.shadow_groups_owner = mr;
    return gm.shadow_groups;
}

/// The cutout pipeline's per-material state, compared by value to skip a
/// rebind across a run of groups sharing a material.
const CutoutBinding = struct {
    fub: types.ShadowMaskFragUB,
    tex: *c.SDL_GPUTexture,
};

/// Per-cascade draw state: the atlas strip being filled, plus what it has
/// already bound or pushed, so a run of material groups sharing a pipeline, a
/// mesh transform or a cutout material issues each call only once.
const Strip = struct {
    cmd: *c.SDL_GPUCommandBuffer,
    pass: *c.SDL_GPURenderPass,
    solid_pipeline: *c.SDL_GPUGraphicsPipeline,
    index: usize,
    bound: ?*c.SDL_GPUGraphicsPipeline = null,
    pushed_lub: ?types.ShadowUB = null,
    bound_cutout: ?CutoutBinding = null,
};

fn bindPipeline(s: *Strip, pl: *c.SDL_GPUGraphicsPipeline) void {
    if (s.bound == pl) return;
    c.SDL_BindGPUGraphicsPipeline(s.pass, pl);
    s.bound = pl;
    // The newly bound pipeline hasn't seen what the previous one was given, so
    // whatever was pushed or bound against it no longer counts as current.
    s.pushed_lub = null;
    s.bound_cutout = null;
}

/// Pushes this mesh's light-space transform unless the strip already has it.
fn pushLightMvp(s: *Strip, lub: types.ShadowUB) void {
    if (s.pushed_lub != null and std.meta.eql(s.pushed_lub.?, lub)) return;
    c.SDL_PushGPUVertexUniformData(s.cmd, 0, &lub, @sizeOf(types.ShadowUB));
    s.pushed_lub = lub;
}

/// Bind the cutout pipeline along with the albedo map and cutoff its discard
/// reads. Returns false when the cutout pipeline is unavailable, leaving the
/// caller to fall back to a solid (quad-shaped) shadow.
fn bindCutout(s: *Strip, sg: *const state.ShadowGroup) bool {
    const pl = state.shadow_mask_pipeline orelse return false;
    const smp = state.sampler orelse return false;
    const tex = sg.albedo orelse return false;
    bindPipeline(s, pl);

    const want = CutoutBinding{ .fub = sg.fub, .tex = tex };
    if (s.bound_cutout != null and std.meta.eql(s.bound_cutout.?, want)) return true;
    c.SDL_PushGPUFragmentUniformData(s.cmd, 0, &want.fub, @sizeOf(types.ShadowMaskFragUB));
    c.SDL_BindGPUFragmentSamplers(s.pass, 0, &[_]c.SDL_GPUTextureSamplerBinding{
        .{ .texture = want.tex, .sampler = smp },
    }, 1);
    s.bound_cutout = want;
    return true;
}

/// Draw one mesh renderer's shadow-casting geometry into `s`, one material
/// group at a time so each group's occlusion mode picks its own pipeline.
fn drawCaster(s: *Strip, cascade: Cascade, obj: *const engine.SceneNode, mr: *const engine.MeshRendererComponent) void {
    const guid_str = mr.mesh.slice();
    if (guid_str.len == 0) return;
    const gm = assets.findGpuMesh(guid_str) orelse return;
    if (gm.idx_count == 0 or gm.material_groups.len == 0) return;

    const t = &obj.transform;
    const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
        .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
        .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
    const lub = types.ShadowUB{ .light_mvp = cascade.vp.multiply(mdl).m };

    c.SDL_BindGPUVertexBuffers(s.pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
    c.SDL_BindGPUIndexBuffer(s.pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

    // Draw only the submeshes this cascade's own cull marked visible (one
    // indirect multi-draw per group); fall back to plain per-submesh draws if
    // that cull didn't run for this mesh (e.g. its compute buffers failed to
    // create).
    const indirect: ?*c.SDL_GPUBuffer = if (gm.shadow_cull_dispatched_frame[s.index] == state.frame_seq)
        gm.shadow_indirect_bufs[s.index]
    else
        null;

    const shadow_groups = shadowGroupsFor(gm, mr);
    if (shadow_groups.len != gm.material_groups.len) return;

    for (gm.material_groups, shadow_groups) |group, *sg| {
        switch (sg.occlusion) {
            .none => continue,
            .solid => bindPipeline(s, s.solid_pipeline),
            .cutout => if (!bindCutout(s, sg)) bindPipeline(s, s.solid_pipeline),
        }
        // After the pipeline bind, which invalidates it on a switch: a group
        // that changed pipeline must still see this mesh's light-space transform.
        pushLightMvp(s, lub);

        const cutout = sg.occlusion == .cutout;
        if (indirect) |ib| {
            c.SDL_DrawGPUIndexedPrimitivesIndirect(s.pass, ib, group.start * @sizeOf(c.SDL_GPUIndexedIndirectDrawCommand), group.count);
            engine.Profiler.countDraw(group.index_count / 3, group.index_count, cutout);
            engine.Profiler.countIndirectCommands(group.count);
            engine.Profiler.countSubmeshesDrawn(group.count);
            continue;
        }
        for (gm.submeshes[group.start..][0..group.count]) |sm| {
            if (sm.index_count == 0) continue;
            c.SDL_DrawGPUIndexedPrimitives(s.pass, sm.index_count, 1, sm.index_offset, 0, 0);
            engine.Profiler.countDraw(sm.index_count / 3, sm.index_count, cutout);
            engine.Profiler.countSubmeshesDrawn(1);
        }
    }
}

/// Render scene depth from the light's point of view into each cascade's
/// strip of the shadow atlas (see `pipeline.createShadowMap`): one render
/// pass over the whole atlas, each cascade's draws restricted to its own
/// viewport/scissor strip and its own GPU-culled indirect buffer (see `gpu_cull.dispatchShadowCulls`).
pub fn renderShadowPass(cmd: *c.SDL_GPUCommandBuffer, cascades: [NUM_CASCADES]Cascade, objects: []const engine.SceneNode) void {
    const shadow_map = state.shadow_map orelse return;
    const solid_pipeline = state.shadow_pipeline orelse return;

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = shadow_map;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, null, 0, &depth_info) orelse return;
    defer c.SDL_EndGPURenderPass(pass);

    const dim = state.features.shadow_dim;
    for (cascades[0..activeCascades()], 0..) |cascade, strip| {
        const y_off: i32 = @intCast(strip * @as(usize, dim));
        c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
            .x = 0,
            .y = @floatFromInt(y_off),
            .w = @floatFromInt(dim),
            .h = @floatFromInt(dim),
            .min_depth = 0.0,
            .max_depth = 1.0,
        });
        c.SDL_SetGPUScissor(pass, &c.SDL_Rect{
            .x = 0,
            .y = y_off,
            .w = @intCast(dim),
            .h = @intCast(dim),
        });

        var s = Strip{ .cmd = cmd, .pass = pass, .solid_pipeline = solid_pipeline, .index = strip };
        for (objects) |*obj| {
            if (!obj.active) continue;
            for (obj.components[0..obj.component_count]) |*comp| {
                if (comp.* != .mesh_renderer) continue;
                if (!comp.mesh_renderer.cast_shadows) continue;
                drawCaster(&s, cascade, obj, &comp.mesh_renderer);
            }
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// A camera looking horizontally from `pos` at `yaw` degrees, matching how
/// `root.zig` builds the view matrix from a node's Euler rotation.
fn testView(pos: Vector3, yaw_deg: f32) Matrix4 {
    const fwd = Matrix4.rotationEuler(0, yaw_deg, 0).transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    const target = Vector3{ .x = pos.x + fwd.x, .y = pos.y + fwd.y, .z = pos.z + fwd.z };
    return Matrix4.lookAt(pos, target, .{ .x = 0, .y = 1, .z = 0 });
}

/// A cascade's world-space half-extent across the light axis, recovered from
/// the length of its view-projection's first row — `vp` is an orthographic
/// projection times an orthonormal view, so that length is exactly `1/radius`.
fn cascadeRadius(vp: Matrix4) f32 {
    const x = vp.m[0];
    const y = vp.m[4];
    const z = vp.m[8];
    return 1.0 / @sqrt(x * x + y * y + z * z);
}

const test_sun = Vector3{ .x = -0.354, .y = -0.707, .z = -0.612 };
const test_bounds = Bounds{ .center = .{ .x = 0, .y = 8, .z = 0 }, .radius = 40 };

test "cascade footprint does not change as the camera turns" {
    // The sphere fit exists so the map's footprint depends on the sub-frustum's
    // shape alone; an AABB fit would resize with yaw and make edges shimmer.
    const base = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, 0), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
    for ([_]f32{ 37, 90, 211, 315 }) |yaw| {
        const turned = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, yaw), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
        for (base, turned) |b, t| {
            try std.testing.expectApproxEqRel(cascadeRadius(b.vp), cascadeRadius(t.vp), 1e-4);
            try std.testing.expectApproxEqRel(b.far_distance, t.far_distance, 1e-5);
        }
    }
}

test "a caster far above a cascade still lies inside its clip volume" {
    // A building standing outside a near cascade's own sphere must still reach
    // the depth pass, or the ground below it is lit through the wall.
    const cascades = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, 0), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
    const roof = Vector3{ .x = 0, .y = 20, .z = 6 };
    for (cascades) |cs| {
        const clip = cs.vp.transformPoint(roof);
        try std.testing.expect(clip.z >= -1.0);
        try std.testing.expect(clip.z <= 1.0);
    }
}

test "a reduced cascade count still spans the whole shadow range" {
    // Dropping cascades is a quality lever, not a coverage one: the last active
    // cascade must still reach the same far distance, or distant geometry
    // silently stops receiving shadows.
    defer state.features = .{};
    const full = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, 0), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
    const full_far = full[NUM_CASCADES - 1].far_distance;

    for ([_]u32{ 1, 2, 3 }) |n| {
        state.features = .{ .cascade_count = n };
        try std.testing.expectEqual(@as(usize, n), activeCascades());
        const cs = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, 0), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
        try std.testing.expectApproxEqRel(full_far, cs[n - 1].far_distance, 1e-5);
        var prev: f32 = 0;
        for (cs[0..n]) |cascade| {
            try std.testing.expect(cascade.far_distance > prev);
            prev = cascade.far_distance;
        }
    }
}

test "splits stay ordered and keep the first cascade tight" {
    // A camera near plane of 0.01 collapses the log term, which used to leave
    // the uniform split alone to size cascade 0 at a quarter of the whole range.
    const cascades = computeCascades(testView(.{ .x = 0, .y = 2, .z = 0 }, 0), 60, 16.0 / 9.0, 0.01, 1000, test_sun, test_bounds);
    var prev: f32 = 0;
    for (cascades) |cs| {
        try std.testing.expect(cs.far_distance > prev);
        prev = cs.far_distance;
    }
    const shadow_far = cascades[NUM_CASCADES - 1].far_distance;
    try std.testing.expect(cascades[0].far_distance < shadow_far * 0.12);
}

test "opaque materials occlude solidly" {
    try std.testing.expectEqual(Occlusion.solid, occlusionFor(.{}));
    try std.testing.expectEqual(Occlusion.solid, occlusionFor(.{ .cull = .none, .depth_write = false }));
}

test "masked materials occlude through the cutout test" {
    try std.testing.expectEqual(Occlusion.cutout, occlusionFor(.{ .alpha_mask = true }));
}

test "blended materials do not occlude" {
    try std.testing.expectEqual(Occlusion.none, occlusionFor(.{ .blend = .alpha }));
    try std.testing.expectEqual(Occlusion.none, occlusionFor(.{ .blend = .additive }));
    // Blending wins over the mask: a blended surface transmits light everywhere.
    try std.testing.expectEqual(Occlusion.none, occlusionFor(.{ .blend = .alpha, .alpha_mask = true }));
}
