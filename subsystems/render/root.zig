//! Hardware 3D scene renderer (SDL3 GPU: Vulkan/Metal/D3D12), shared by the
//! editor viewport and the shipped game. UI-toolkit independent: the caller
//! supplies a GPU device, a color-target texture, the scene nodes, and asset
//! bytes via the source callbacks. The editor draws into an offscreen target;
//! the game draws into the swapchain.
//!
//! Code is split by theme: `types` (plain data), `state` (GPU singletons),
//! `pipeline` (device resource creation), `assets` (GUID→GPU upload + material
//! resolution), `shadow` (shadow mapping). This file is the orchestration.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");

const types = @import("types.zig");
const state = @import("state.zig");
const pipeline = @import("pipeline.zig");
const assets = @import("assets.zig");
const shadow = @import("shadow.zig");
const gizmos = @import("gizmos.zig");

const log = std.log.scoped(.render);

const c = gpu.c;
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

pub const Bytes = types.Bytes;
pub const SourceFn = types.SourceFn;

/// Gizmo line vertex and uniform types, re-exported for the editor overlay.
pub const GizmoVertex = gizmos.GizmoVertex;
pub const GizmoUB = gizmos.GizmoUB;

/// Resolved scene camera: the same view/projection the renderer uses, exposed so
/// the editor can build picking rays and draw gizmos that line up exactly.
pub const Camera = struct {
    pos: Vector3,
    rotation: Vector3,
    fov: f32,
    near: f32,
    far: f32,
    view: Matrix4,
    proj: Matrix4,
    /// proj * view.
    view_proj: Matrix4,
};

/// A free-look camera pose the editor can impose on the viewport, independent of
/// any scene camera component.
pub const EditorCam = types.EditorCam;

/// Set (or clear) the editor free-look camera override. Cleared in Play mode so
/// the running game's own camera drives the viewport again.
pub fn setEditorCamera(cam: ?EditorCam) void {
    state.editor_cam = cam;
}

/// The current editor free-look camera override, if any. Lets a caller that
/// temporarily imposes its own camera (e.g. an asset preview renderer) save and
/// restore the viewport's camera around its own render.
pub fn editorCamera() ?EditorCam {
    return state.editor_cam;
}

/// Serve `bytes` for `guid` from `resolveMaterial` instead of `material_src`,
/// until cleared. Used by live-editing panels (e.g. the material inspector) to
/// preview unsaved edits without writing to disk every frame.
pub fn setMaterialOverride(guid: []const u8, bytes: []const u8) void {
    state.material_override_key_len = @min(guid.len, state.OVERRIDE_KEY_CAP);
    @memcpy(state.material_override_key[0..state.material_override_key_len], guid[0..state.material_override_key_len]);
    state.material_override_bytes = bytes;
}

/// Stop overriding any material GUID.
pub fn clearMaterialOverride() void {
    state.material_override_key_len = 0;
    state.material_override_bytes = &.{};
}

/// Compute the camera used to render `objects` at `w`×`h`. Uses the editor
/// free-look override if set, otherwise the first active camera component (or a
/// default if none). Mirrors the camera setup in `renderScene`.
pub fn sceneCamera(w: u32, h: u32, objects: []const engine.SceneNode) Camera {
    var cam_pos = Vector3{ .x = 0, .y = 2, .z = -5 };
    var cam_rot = Vector3{};
    var cam_fov: f32 = 60.0;
    var cam_near: f32 = 0.01;
    var cam_far: f32 = 1000.0;
    var ortho_half_height: f32 = 0;
    if (state.editor_cam) |ec| {
        cam_pos = ec.pos;
        cam_rot = ec.rot;
        cam_fov = ec.fov;
        cam_near = ec.near;
        cam_far = ec.far;
        ortho_half_height = ec.ortho_half_height;
    } else cam_search: for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .camera) {
                cam_pos = obj.transform.position;
                cam_rot = obj.transform.rotation;
                cam_fov = comp.camera.fov;
                cam_near = comp.camera.near;
                cam_far = comp.camera.far;
                break :cam_search;
            }
        }
    }
    const rm = Matrix4.rotationEuler(cam_rot.x, cam_rot.y, cam_rot.z);
    const fwd_v = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    const look_at = Vector3{ .x = cam_pos.x + fwd_v.x, .y = cam_pos.y + fwd_v.y, .z = cam_pos.z + fwd_v.z };
    const view = Matrix4.lookAt(cam_pos, look_at, .{ .x = 0, .y = 1, .z = 0 });
    const asp = @as(f32, @floatFromInt(w)) / @as(f32, @floatFromInt(@max(h, 1)));
    const proj = if (ortho_half_height > 0) blk: {
        const hh = ortho_half_height;
        const hw = hh * asp;
        break :blk Matrix4.orthographic(-hw, hw, -hh, hh, cam_near, cam_far);
    } else Matrix4.perspective(cam_fov, asp, cam_near, cam_far);
    return .{
        .pos = cam_pos,
        .rotation = cam_rot,
        .fov = cam_fov,
        .near = cam_near,
        .far = cam_far,
        .view = view,
        .proj = proj,
        .view_proj = proj.multiply(view),
    };
}

/// Draw recorded gizmo line vertices over the already-rendered scene. `overlay`
/// selects the always-on-top pipeline (manipulation handles) vs. depth-tested
/// world gizmos. Call after `renderScene`, outside any render pass.
pub fn renderGizmos(
    cmd: *c.SDL_GPUCommandBuffer,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    view_proj: [16]f32,
    verts: []const GizmoVertex,
    overlay: bool,
) void {
    gizmos.renderGizmos(cmd, color_tex, w, h, view_proj, verts, overlay);
}

/// Register the GUID→bytes callbacks (mesh / texture / material). Call once
/// before rendering.
pub fn setSources(mesh: SourceFn, texture: SourceFn, material: SourceFn) void {
    state.mesh_src = mesh;
    state.texture_src = texture;
    state.material_src = material;
}

/// Initialize the renderer on a GPU device (must accept SPIR-V shaders).
pub fn init(device: *c.SDL_GPUDevice) !void {
    state.device = device;
    state.pipeline = try pipeline.createPipeline(device);
    state.sampler = try pipeline.createSampler(device);
    state.shadow_sampler = pipeline.createShadowSampler(device) catch |err| s: {
        log.warn("shadow sampler failed: {any} — shadows disabled.", .{err});
        break :s null;
    };
    state.shadow_pipeline = pipeline.createShadowPipeline(device) catch |err| p: {
        log.warn("shadow pipeline failed: {any} — shadows disabled.", .{err});
        break :p null;
    };
    state.gizmo_pipeline = gizmos.createGizmoPipeline(device, true) catch |err| g: {
        log.warn("gizmo pipeline failed: {any} — gizmos disabled.", .{err});
        break :g null;
    };
    state.gizmo_overlay_pipeline = gizmos.createGizmoPipeline(device, false) catch |err| g: {
        log.warn("gizmo overlay pipeline failed: {any} — handles disabled.", .{err});
        break :g null;
    };
    log.info("Ready (SPIRV).", .{});
}

/// Depth texture for a `w`×`h` pass, cached by size (see `state.depth_targets`).
/// Reuses a same-size texture, fills an empty slot, or evicts the round-robin
/// slot when full. Eviction only frees a size not requested in the current
/// frame — the distinct sizes rendered per frame (viewport + previews) stay well
/// under `MAX_DEPTH_TARGETS` — so it never releases a depth attachment an
/// in-flight command buffer still references.
fn depthFor(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?*c.SDL_GPUTexture {
    if (state.findDepth(w, h)) |t| return t;

    for (&state.depth_targets) |*d| {
        if (d.tex == null) {
            d.tex = pipeline.createDepth(dev, w, h) catch return null;
            d.w = w;
            d.h = h;
            return d.tex;
        }
    }

    const d = &state.depth_targets[state.depth_evict_cursor];
    state.depth_evict_cursor = (state.depth_evict_cursor + 1) % state.depth_targets.len;
    if (d.tex) |old| c.SDL_ReleaseGPUTexture(dev, old);
    d.* = .{ .tex = pipeline.createDepth(dev, w, h) catch {
        d.* = .{};
        return null;
    }, .w = w, .h = h };
    return d.tex;
}

fn destroyDepthTargets(dev: *c.SDL_GPUDevice) void {
    for (&state.depth_targets) |*d| {
        if (d.tex) |t| c.SDL_ReleaseGPUTexture(dev, t);
        d.* = .{};
    }
    state.depth_evict_cursor = 0;
}

/// Render `objects` into `color_tex` (a `w`×`h` target) using `cmd`. The caller
/// owns the command buffer and the color target (an editor offscreen texture or
/// the game swapchain) and submits the command buffer itself.
pub fn renderScene(
    cmd: *c.SDL_GPUCommandBuffer,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    objects: []const engine.SceneNode,
) void {
    var scene_zone = engine.Profiler.zone("render.scene");
    defer scene_zone.end();

    const pl = state.pipeline orelse return;
    const dev = state.device orelse return;
    const sampler = state.sampler orelse return;
    if (w == 0 or h == 0) return;

    if (state.white_tex == null)
        state.white_tex = pipeline.createSolidTexture(cmd, dev, .{ 255, 255, 255, 255 }) catch null;
    if (state.flat_normal_tex == null)
        state.flat_normal_tex = pipeline.createSolidTexture(cmd, dev, .{ 128, 128, 255, 255 }) catch null;
    if (state.shadow_map == null)
        state.shadow_map = pipeline.createShadowMap(dev) catch null;

    const depth_tex = depthFor(dev, w, h) orelse {
        log.err("depth target failed", .{});
        return;
    };

    {
        var upload_zone = engine.Profiler.zone("render.upload");
        defer upload_zone.end();
        const tex_before = state.texture_count;
        assets.uploadNewAssets(cmd, dev, objects);
        engine.Profiler.countTexturesCreated(@intCast(state.texture_count -| tex_before));
    }

    // ── Camera ──────────────────────────────────────────────────────────────
    const cam = sceneCamera(w, h, objects);
    const cam_pos = cam.pos;
    const vp = cam.view_proj;

    const ambient = [4]f32{ 0.15, 0.15, 0.18, 0.0 };

    // ── Lights ──────────────────────────────────────────────────────────────
    // The first shadow-casting directional light (kept at slot 0) drives shadows.
    var lights = [_]types.GpuLight{.{}} ** types.MAX_LIGHTS;
    var light_count: usize = 0;
    var shadow_dir: ?Vector3 = null;
    for (objects) |*obj| {
        if (!obj.active or light_count >= types.MAX_LIGHTS) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .light) continue;
            const lc = &comp.light;
            const lrm = Matrix4.rotationEuler(obj.transform.rotation.x, obj.transform.rotation.y, obj.transform.rotation.z);
            const d = lrm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
            const ty: f32 = switch (lc.kind) {
                .directional => 0,
                .point => 1,
                .spot => 2,
            };
            const p = obj.transform.position;
            const cos_outer = @cos(lc.spot_angle * std.math.pi / 180.0);
            const cos_inner = @cos(lc.spot_angle * (1.0 - lc.spot_softness) * std.math.pi / 180.0);

            const slot = if (lc.kind == .directional and lc.cast_shadows and shadow_dir == null) blk: {
                shadow_dir = .{ .x = d.x, .y = d.y, .z = d.z };
                if (light_count > 0) lights[light_count] = lights[0];
                break :blk 0;
            } else light_count;

            lights[slot] = .{
                .position = .{ p.x, p.y, p.z, ty },
                .direction = .{ d.x, d.y, d.z, lc.range },
                .color = .{ lc.color_r, lc.color_g, lc.color_b, lc.intensity },
                .cone = .{ cos_outer, cos_inner, 0, 0 },
            };
            light_count += 1;
        }
    }

    const bounds = shadow.sceneBounds(objects);
    const light_vp = if (shadow_dir) |sd| shadow.shadowMatrix(sd, bounds) else Matrix4{};
    const shadows_on = shadow_dir != null and state.shadow_map != null and state.shadow_sampler != null and state.shadow_pipeline != null;

    if (shadows_on) {
        var shadow_zone = engine.Profiler.zone("render.shadow");
        defer shadow_zone.end();
        shadow.renderShadowPass(cmd, light_vp, objects);
    }

    // ── Main pass ───────────────────────────────────────────────────────────
    var main_zone = engine.Profiler.zone("render.main");
    defer main_zone.end();
    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = color_tex;
    color_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;
    color_info.clear_color = .{ .r = 0.14, .g = 0.14, .b = 0.16, .a = 1.0 };

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = depth_tex;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    // Preserve depth so the gizmo overlay pass can depth-test against the scene.
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return;
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    // Track the previously bound material GUID so we can count pipeline/material
    // switches for the profiler (a draw with the same material is "free").
    var prev_mat: []const u8 = "";
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const gm = assets.findGpuMesh(guid_str) orelse continue;
            if (gm.submesh_count == 0) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            const mvp = vp.multiply(mdl);

            const vub = types.VertexUB{ .mvp = mvp.m, .model = mdl.m };
            c.SDL_PushGPUVertexUniformData(cmd, 0, &vub, @sizeOf(types.VertexUB));

            c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
            c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);

            const mr = &comp.mesh_renderer;
            const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_SUBMESH_MATERIALS);
            const receives = mr.receive_shadows and shadows_on;

            for (gm.submeshes[0..gm.submesh_count], 0..) |sm, si| {
                if (sm.index_count == 0) continue;
                const mat_guid = if (si < mat_n) mr.materials[si].slice() else "";
                if (!std.mem.eql(u8, mat_guid, prev_mat)) {
                    engine.Profiler.countMaterialSwitch();
                    prev_mat = mat_guid;
                }

                const mat_res = assets.resolveMaterial(mat_guid);

                const white = state.white_tex orelse continue;
                const flat_n = state.flat_normal_tex orelse white;
                const albedo_t = assets.pickTexture(mat_res.map(.albedo), white);
                const mr_t = assets.pickTexture(mat_res.map(.mr), white);
                const normal_t = assets.pickTexture(mat_res.map(.normal), flat_n);
                const emis_t = assets.pickTexture(mat_res.map(.emissive), white);
                const occ_t = assets.pickTexture(mat_res.map(.occlusion), white);

                const fub = types.FragUB{
                    .ambient_color = ambient,
                    .camera_pos = .{ cam_pos.x, cam_pos.y, cam_pos.z, @floatFromInt(light_count) },
                    .base_color = mat_res.base_color,
                    .mr_ns_oc = .{ mat_res.metallic, mat_res.roughness, mat_res.normal_scale, mat_res.occlusion_strength },
                    .emissive = .{ mat_res.emissive[0], mat_res.emissive[1], mat_res.emissive[2], mat_res.emissive_strength },
                    .flags = .{ assets.present(albedo_t.found), assets.present(mr_t.found), assets.present(normal_t.found), assets.present(emis_t.found) },
                    .flags2 = .{ assets.present(occ_t.found), mat_res.alpha_cutoff, 0.0, assets.present(receives) },
                    .light_vp = light_vp.m,
                    .lights = lights,
                };
                c.SDL_PushGPUFragmentUniformData(cmd, 0, &fub, @sizeOf(types.FragUB));

                const shadow_tex = state.shadow_map orelse white;
                const shadow_smp = state.shadow_sampler orelse sampler;
                const bindings = [_]c.SDL_GPUTextureSamplerBinding{
                    .{ .texture = albedo_t.tex, .sampler = sampler },
                    .{ .texture = mr_t.tex, .sampler = sampler },
                    .{ .texture = normal_t.tex, .sampler = sampler },
                    .{ .texture = emis_t.tex, .sampler = sampler },
                    .{ .texture = occ_t.tex, .sampler = sampler },
                    .{ .texture = shadow_tex, .sampler = shadow_smp },
                };
                c.SDL_BindGPUFragmentSamplers(pass, 0, &bindings, 6);

                c.SDL_DrawGPUIndexedPrimitives(pass, sm.index_count, 1, sm.index_offset, 0, 0);
                // Every mesh binds samplers, so this draw always counts as textured.
                engine.Profiler.countDraw(sm.index_count / 3, sm.index_count, true);
            }
        }
    }

    c.SDL_EndGPURenderPass(pass);
}

/// Release all GPU resources.
pub fn deinit() void {
    const dev = state.device orelse return;
    gizmos.deinit(dev);
    destroyDepthTargets(dev);
    if (state.white_tex) |t| c.SDL_ReleaseGPUTexture(dev, t);
    if (state.flat_normal_tex) |t| c.SDL_ReleaseGPUTexture(dev, t);
    state.white_tex = null;
    state.flat_normal_tex = null;
    for (state.textures[0..state.texture_count]) |*gt|
        c.SDL_ReleaseGPUTexture(dev, gt.texture);
    state.texture_count = 0;
    for (state.meshes[0..state.mesh_count]) |*gm| {
        c.SDL_ReleaseGPUBuffer(dev, gm.vtx_buf);
        c.SDL_ReleaseGPUBuffer(dev, gm.idx_buf);
    }
    state.mesh_count = 0;
    if (state.shadow_map) |t| c.SDL_ReleaseGPUTexture(dev, t);
    if (state.shadow_sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    if (state.shadow_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    state.shadow_map = null;
    state.shadow_sampler = null;
    state.shadow_pipeline = null;
    if (state.sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    if (state.pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    state.sampler = null;
    state.pipeline = null;
}

test {
    std.testing.refAllDecls(@This());
    _ = types;
    _ = state;
}
