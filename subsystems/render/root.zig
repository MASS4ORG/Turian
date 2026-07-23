//! Hardware 3D scene renderer (SDL3 GPU: Vulkan/Metal/D3D12), shared by the
//! editor viewport and the shipped game. Caller supplies GPU device, target
//! texture, scene nodes, and asset bytes via source callbacks. Split across
//! `types`, `state`, `pipeline`, `assets`, `shadow`; this file orchestrates.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");

const types = @import("types.zig");
const state = @import("state.zig");
const pipeline = @import("pipeline.zig");
const assets = @import("assets.zig");
const shadow = @import("shadow.zig");
const gizmos = @import("gizmos.zig");
const culling = @import("culling.zig");
const gpu_timing = @import("gpu_timing.zig");
const draw = @import("draw.zig");

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

/// Enable/disable fence-bracketed per-pass GPU timing; off by default (pipeline stall).
pub fn setDetailedGpuTiming(on: bool) void {
    state.detailed_gpu_timing = on;
}

/// Whether detailed GPU timing is currently enabled.
pub fn detailedGpuTiming() bool {
    return state.detailed_gpu_timing;
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

/// Compute the camera used for rendering; editor override or first active camera component.
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
    state.skybox_pipeline = pipeline.createSkyboxPipeline(device) catch |err| p: {
        log.warn("skybox pipeline failed: {any} — environment background disabled.", .{err});
        break :p null;
    };
    log.info("Ready (SPIRV).", .{});
}

/// Depth texture for a `w`x`h` pass, cached by size.
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

/// Render `objects` into `color_tex` using `cmd`.
pub fn renderScene(
    cmd: *c.SDL_GPUCommandBuffer,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    objects: []const engine.SceneNode,
) void {
    var scene_zone = engine.Profiler.zone("render.scene");
    defer scene_zone.end();

    const dev = state.device orelse return;
    const sampler = state.sampler orelse return;
    if (w == 0 or h == 0) return;
    state.frame_seq += 1;

    if (state.white_tex == null)
        state.white_tex = pipeline.createSolidTexture(cmd, dev, .{ 255, 255, 255, 255 }) catch null;
    if (state.flat_normal_tex == null)
        state.flat_normal_tex = pipeline.createSolidTexture(cmd, dev, .{ 128, 128, 255, 255 }) catch null;
    if (state.shadow_map == null)
        state.shadow_map = pipeline.createShadowMap(dev) catch null;
    if (state.cull_pipeline == null)
        state.cull_pipeline = pipeline.createCullComputePipeline(dev) catch |err| p: {
            log.warn("cull compute pipeline failed: {any} — falling back to per-submesh CPU culling.", .{err});
            break :p null;
        };

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
    const frustum = culling.Frustum.extract(vp);

    // ── Environment (IBL + skybox) ───────────────────────────────────────────
    // At most one active EnvironmentComponent is used per scene (first found).
    var env_tex: ?*state.GpuTexture = null;
    var env_intensity: f32 = 1.0;
    var env_show_skybox: bool = true;
    env_scan: for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .environment) continue;
            const guid = comp.environment.env_map.slice();
            if (guid.len == 0) continue;
            if (assets.findGpuTexture(guid)) |gt| {
                env_tex = gt;
                env_intensity = comp.environment.intensity;
                env_show_skybox = comp.environment.show_skybox;
            }
            break :env_scan;
        }
    }

    var env_params = [4]f32{ 0, 0, 0, 0 };
    var env_sh = [_][4]f32{.{ 0, 0, 0, 0 }} ** 9;
    if (env_tex) |gt| {
        if (gt.env) |ed| {
            env_params = .{ env_intensity, @floatFromInt(ed.mip_count), 1.0, 0.0 };
            for (0..9) |i| env_sh[i] = .{ ed.sh[i][0], ed.sh[i][1], ed.sh[i][2], 0.0 };
        }
    }

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
        gpu_timing.runShadowPass(dev, cmd, light_vp, objects);
    }

    // ── GPU-driven cull compute phase ────────────────────────────────────────
    // Must finish before the main render pass (compute and render passes can't overlap on the same command buffer).
    gpu_timing.runCullPhase(dev, cmd, objects, frustum);

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
    // Preserve depth for the gizmo overlay pass.
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return;
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    const white = state.white_tex orelse {
        c.SDL_EndGPURenderPass(pass);
        return;
    };
    const flat_n = state.flat_normal_tex orelse white;
    const shadow_tex = state.shadow_map orelse white;
    const shadow_smp = state.shadow_sampler orelse sampler;
    const env_gpu_tex = if (env_tex) |gt| gt.texture else white;
    const fu = draw.FrameUniforms{
        .cam_pos4 = .{ cam_pos.x, cam_pos.y, cam_pos.z, @floatFromInt(light_count) },
        .light_vp = light_vp.m,
        .lights = lights,
        .env_params = env_params,
        .env_sh = env_sh,
    };

    // Draw the environment background first so opaque geometry overwrites sky pixels.
    if (env_tex != null and env_show_skybox) {
        if (state.skybox_pipeline) |skybox_pl| {
            c.SDL_BindGPUGraphicsPipeline(pass, skybox_pl);
            const skybox_fub = types.SkyboxFragUB{
                .inv_view_proj = vp.inverse().m,
                .camera_pos_intensity = .{ cam_pos.x, cam_pos.y, cam_pos.z, env_intensity },
            };
            c.SDL_PushGPUFragmentUniformData(cmd, 0, &skybox_fub, @sizeOf(types.SkyboxFragUB));
            c.SDL_BindGPUFragmentSamplers(pass, 0, &[_]c.SDL_GPUTextureSamplerBinding{
                .{ .texture = env_gpu_tex, .sampler = sampler },
            }, 1);
            c.SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
        }
    }

    var bound_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
    draw.transparent_count = 0;

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
            if (gm.submeshes.len == 0) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));

            if (culling.aabbOutsideFrustum(gm.bounds_min, gm.bounds_max, mdl, frustum)) {
                engine.Profiler.countSubmeshesCulled(@intCast(gm.submeshes.len));
                continue;
            }

            const mvp = vp.multiply(mdl);
            const vub = types.VertexUB{ .mvp = mvp.m, .model = mdl.m };

            const mr = &comp.mesh_renderer;
            const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);
            const receives = mr.receive_shadows and shadows_on;
            const dctx = draw.DrawCtx{ .shadow_tex = shadow_tex, .shadow_smp = shadow_smp, .env_gpu_tex = env_gpu_tex, .white = white, .flat_n = flat_n, .sampler = sampler };

            // GPU-driven indirect path (cull compute dispatched this frame).
            if (gm.indirect_buf != null and gm.cull_dispatched_frame == state.frame_seq) {
                for (gm.material_groups) |group| {
                    const mat_guid = draw.materialGuidForSlot(mr, mat_n, group.material_slot);
                    if (!std.mem.eql(u8, mat_guid, prev_mat)) {
                        engine.Profiler.countMaterialSwitch();
                        prev_mat = mat_guid;
                    }
                    const mat_res = assets.resolveMaterial(mat_guid);
                    const dp = draw.buildDrawParams(&mat_res, gm, 0, 0, vub, receives, dctx);

                    // Opaque groups draw as one indirect multi-draw; transparent groups fall back to per-submesh CPU path for depth sort.
                    if (mat_res.render.blend == .disabled) {
                        draw.submitIndirectDraw(cmd, pass, dev, &bound_pipeline, fu, dp, gm.indirect_buf.?, group.start * @sizeOf(c.SDL_GPUIndexedIndirectDrawCommand), group.count);
                    } else {
                        for (gm.submeshes[group.start..][0..group.count]) |sm| {
                            if (sm.index_count == 0) continue;
                            if (culling.aabbOutsideFrustum(sm.bounds_min, sm.bounds_max, mdl, frustum)) {
                                engine.Profiler.countSubmeshesCulled(1);
                                continue;
                            }
                            engine.Profiler.countSubmeshesDrawn(1);
                            var tdp = dp;
                            tdp.index_offset = sm.index_offset;
                            tdp.index_count = sm.index_count;
                            if (draw.transparent_count < draw.transparent_draws.len) {
                                draw.transparent_draws[draw.transparent_count] = .{ .params = tdp, .sort_depth = Vector3.distanceSquared(cam_pos, t.position) };
                                draw.transparent_count += 1;
                            }
                        }
                    }
                }
                continue;
            }

            // CPU fallback: per-submesh cull + draw.
            for (gm.submeshes) |sm| {
                if (sm.index_count == 0) continue;
                if (culling.aabbOutsideFrustum(sm.bounds_min, sm.bounds_max, mdl, frustum)) {
                    engine.Profiler.countSubmeshesCulled(1);
                    continue;
                }
                engine.Profiler.countSubmeshesDrawn(1);

                const mat_guid = draw.materialGuidForSlot(mr, mat_n, sm.material_slot);
                if (!std.mem.eql(u8, mat_guid, prev_mat)) {
                    engine.Profiler.countMaterialSwitch();
                    prev_mat = mat_guid;
                }

                const mat_res = assets.resolveMaterial(mat_guid);
                const dp = draw.buildDrawParams(&mat_res, gm, sm.index_offset, sm.index_count, vub, receives, dctx);

                // Opaque draws right away; blended/additive draws are deferred for back-to-front sort.
                if (mat_res.render.blend == .disabled) {
                    draw.submitDraw(cmd, pass, dev, &bound_pipeline, fu, dp);
                } else if (draw.transparent_count < draw.transparent_draws.len) {
                    draw.transparent_draws[draw.transparent_count] = .{
                        .params = dp,
                        .sort_depth = Vector3.distanceSquared(cam_pos, t.position),
                    };
                    draw.transparent_count += 1;
                }
            }
        }
    }

    // Back-to-front so alpha compositing reads correctly (farthest drawn first).
    {
        var transparent_zone = engine.Profiler.zone("render.transparent");
        defer transparent_zone.end();
        std.sort.pdq(draw.TransparentDraw, draw.transparent_draws[0..draw.transparent_count], {}, draw.transparentFartherFirst);
        for (draw.transparent_draws[0..draw.transparent_count]) |td|
            draw.submitDraw(cmd, pass, dev, &bound_pipeline, fu, td.params);
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
        if (gm.bounds_buf) |bb| c.SDL_ReleaseGPUBuffer(dev, bb);
        if (gm.indirect_buf) |ib| c.SDL_ReleaseGPUBuffer(dev, ib);
        std.heap.page_allocator.free(gm.submeshes);
        std.heap.page_allocator.free(gm.material_groups);
    }
    state.mesh_count = 0;
    if (state.shadow_map) |t| c.SDL_ReleaseGPUTexture(dev, t);
    if (state.shadow_sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    if (state.shadow_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    state.shadow_map = null;
    state.shadow_sampler = null;
    state.shadow_pipeline = null;
    if (state.skybox_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    state.skybox_pipeline = null;
    if (state.cull_pipeline) |p| c.SDL_ReleaseGPUComputePipeline(dev, p);
    state.cull_pipeline = null;
    if (state.sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    draw.destroyScenePipelines(dev);
    state.sampler = null;
}

test {
    std.testing.refAllDecls(@This());
    _ = types;
    _ = state;
}
