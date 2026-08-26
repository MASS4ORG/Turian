//! Static reflection probes: baked local cubemaps filling what SSR (`ssr.zig`)
//! structurally can't — off-screen, occluded, or interior surfaces that never
//! appear in the screen-space history buffer. `bakeDirty` captures the scene
//! into 6 cube faces from a placed `ReflectionProbeComponent`'s position, then
//! GGX-prefilters it through the same machinery the global HDRI uses
//! (`ibl_prefilter.zig`). Bake-once and throttled (at most one dirty probe per
//! `bakeDirty` call) — this is not a per-frame pass, unlike SSAO/SSR.
//! `resolveForSubmesh` (CPU-side, cached on `state.GpuSubmesh`) then picks the
//! best-matching baked probe per submesh every frame, mirroring
//! `postprocess.zig`'s volume box/sphere/blend_distance/priority model.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const assets = @import("assets.zig");
const pipeline = @import("pipeline.zig");
const culling = @import("culling.zig");
const draw = @import("draw.zig");
const ibl_prefilter = @import("ibl_prefilter.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;
const log = std.log.scoped(.render);

/// Probe prefilter resolution, deliberately smaller than the global env's
/// 128px/6 mips (`ibl_prefilter.PREFILTER_SIZE`/`PREFILTER_MIP_COUNT`) — local
/// probes are typically viewed obliquely and already roughness-blurred by the
/// same GGX prefilter, so the extra resolution wouldn't be visible, and this
/// bounds VRAM to ~2.3 MB/probe regardless of `MAX_REFLECTION_PROBES`.
const PROBE_PREFILTER_SIZE: u32 = 64;
const PROBE_MIP_COUNT: u32 = 5;

/// Cube-face capture directions, in SDL_GPU's hardware layer order (matches
/// `faceDir` in `shaders/equirect_to_cubemap.frag.glsl`): +X,-X,+Y,-Y,+Z,-Z.
const FACE_DIRS = [6]Vector3{
    .{ .x = 1, .y = 0, .z = 0 },
    .{ .x = -1, .y = 0, .z = 0 },
    .{ .x = 0, .y = 1, .z = 0 },
    .{ .x = 0, .y = -1, .z = 0 },
    .{ .x = 0, .y = 0, .z = 1 },
    .{ .x = 0, .y = 0, .z = -1 },
};
/// Per-face "up" vectors for the capture camera's `Matrix4.lookAt`, the
/// standard cubemap-capture convention (avoids a degenerate lookAt for the
/// +Y/-Y faces, where world-up is parallel to the view direction).
const FACE_UPS = [6]Vector3{
    .{ .x = 0, .y = -1, .z = 0 },
    .{ .x = 0, .y = -1, .z = 0 },
    .{ .x = 0, .y = 0, .z = 1 },
    .{ .x = 0, .y = 0, .z = -1 },
    .{ .x = 0, .y = -1, .z = 0 },
    .{ .x = 0, .y = -1, .z = 0 },
};

/// Global-environment inputs a probe capture blends in as its own background
/// ambient/skybox — never another probe's cubemap, which would create a
/// circular bake dependency. Callers (`root.zig`) already compute all of
/// these once per frame for the main pass; probe capture reuses them as-is.
pub const BakeEnv = struct {
    /// GGX-prefiltered specular cubemap (or the black fallback) — see `state.EnvironmentData`.
    prefiltered_tex: *c.SDL_GPUTexture,
    /// Raw equirect HDRI texture for the skybox background, or null to skip
    /// it (no environment bound this frame).
    raw_tex: ?*c.SDL_GPUTexture,
    show_skybox: bool,
    /// x=intensity, y=mip_count, z=has_env, w unused — matches `FragUB.env_params`.
    params: [4]f32,
    sh: [9][4]f32,
    light_count: usize,
};

var budget_warned: bool = false;

fn createCaptureDepth(dev: *c.SDL_GPUDevice, size: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.SHADOW_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = size,
        .height = size,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.ProbeCaptureDepthCreate;
}

/// Ensures the shared scratch capture targets are at least `size`x`size`,
/// recreating them if a larger (or first-ever) probe needs more room. Probes
/// bake one at a time, sequentially, so one pooled pair bounds VRAM
/// regardless of `MAX_REFLECTION_PROBES`.
fn ensureCaptureTargets(dev: *c.SDL_GPUDevice, size: u32) bool {
    if (state.probe_capture_base == null or state.probe_capture_base_size != size) {
        if (state.probe_capture_base) |t| c.SDL_ReleaseGPUTexture(dev, t);
        state.probe_capture_base = pipeline.createCubemapTexture(dev, size, 1) catch null;
        state.probe_capture_base_size = if (state.probe_capture_base != null) size else 0;
    }
    if (state.probe_capture_depth == null or state.probe_capture_depth_size != size) {
        if (state.probe_capture_depth) |t| c.SDL_ReleaseGPUTexture(dev, t);
        state.probe_capture_depth = createCaptureDepth(dev, size) catch null;
        state.probe_capture_depth_size = if (state.probe_capture_depth != null) size else 0;
    }
    return state.probe_capture_base != null and state.probe_capture_depth != null;
}

/// Render one cube face of a probe's capture: skybox background, then every
/// opaque mesh_renderer culled against this face's own frustum. Mirrors
/// `root.zig`'s main-pass CPU-fallback loop and `prepass.run`'s structure,
/// with shadows/SSAO/SSR all off (a capture isn't a screen) and transparent
/// groups skipped entirely — they're never added to `draw.transparent_draws`,
/// the main pass's own frame-global scratch array, avoiding cross-pass corruption.
fn captureFace(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler_default: *c.SDL_GPUSampler,
    objects: []const engine.SceneNode,
    env: BakeEnv,
    size: u32,
    face: u32,
    pos: Vector3,
    dir: Vector3,
    up: Vector3,
    near: f32,
    far: f32,
) void {
    const base = state.probe_capture_base orelse return;
    const depth = state.probe_capture_depth orelse return;

    const view = Matrix4.lookAt(pos, pos.add(dir), up);
    const proj = Matrix4.perspective(90.0, 1.0, near, far);
    const vp = proj.multiply(view);
    const frustum = culling.Frustum.extract(vp);

    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = base;
    color_info.mip_level = 0;
    color_info.layer_or_depth_plane = face;
    color_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    color_info.clear_color = .{ .r = 0, .g = 0, .b = 0, .a = 1 };
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = depth;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_DONT_CARE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return;
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(size),
        .h = @floatFromInt(size),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    const white = state.white_tex orelse {
        c.SDL_EndGPURenderPass(pass);
        return;
    };
    const flat_n = state.flat_normal_tex orelse white;
    const shadow_tex = state.shadow_map orelse white;
    const shadow_smp = state.shadow_sampler orelse sampler_default;
    const cube_smp = state.cubemap_sampler orelse sampler_default;
    const ssr_fallback = state.ssr_fallback_tex orelse white;

    // Background first, so opaque geometry overwrites sky pixels — same
    // ordering as root.zig's main pass.
    if (env.show_skybox) {
        if (env.raw_tex) |raw_tex| {
            if (state.skybox_pipeline) |skybox_pl| {
                c.SDL_BindGPUGraphicsPipeline(pass, skybox_pl);
                const skybox_fub = types.SkyboxFragUB{
                    .inv_view_proj = vp.inverse().m,
                    .camera_pos_intensity = .{ pos.x, pos.y, pos.z, env.params[0] },
                };
                c.SDL_PushGPUFragmentUniformData(cmd, 0, &skybox_fub, @sizeOf(types.SkyboxFragUB));
                c.SDL_BindGPUFragmentSamplers(pass, 0, &[_]c.SDL_GPUTextureSamplerBinding{
                    .{ .texture = raw_tex, .sampler = sampler_default },
                }, 1);
                c.SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
            }
        }
    }

    if (state.lights_buf) |lb|
        c.SDL_BindGPUFragmentStorageBuffers(pass, 0, &[_]?*c.SDL_GPUBuffer{lb}, 1);

    const fu = draw.FrameUniforms{
        .cam_pos4 = .{ pos.x, pos.y, pos.z, @floatFromInt(env.light_count) },
        .cam_forward4 = .{ dir.x, dir.y, dir.z, 1 },
        .env_params = env.params,
        .env_sh = env.sh,
        .cascade_vp = std.mem.zeroes([types.NUM_CASCADES][16]f32),
        .cascade_splits = .{ 0, 0, 0, 0 },
        .cascade_depth_scale = .{ 0, 0, 0, 0 },
    };
    // Every scene pipeline now declares 2 fragment uniform buffers; this pass
    // must push both even though probe capture has no shadows (cascade_vp is
    // zeroed above) — an unbound slot 1 would be a read into stale/undefined
    // GPU state.
    draw.pushFrameUniforms(cmd, fu);
    const dctx = draw.DrawCtx{
        .shadow_tex = shadow_tex,
        .shadow_smp = shadow_smp,
        .env_prefiltered_tex = env.prefiltered_tex,
        .cubemap_smp = cube_smp,
        .white = white,
        .flat_n = flat_n,
        .sampler = sampler_default,
        .ssao_tex = white,
        .ssao_smp = cube_smp,
        .ssr_tex = ssr_fallback,
    };

    var bind_state = draw.BindState{};
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
            const vub = types.VertexUB{ .mvp = mvp.m, .model = mdl.m };
            const mr = &comp.mesh_renderer;
            const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);

            for (gm.submeshes) |sm| {
                if (sm.index_count == 0) continue;
                if (culling.aabbOutsideFrustum(sm.bounds_min, sm.bounds_max, mdl, frustum)) continue;

                const mat_guid = draw.materialGuidForSlot(mr, mat_n, sm.material_slot);
                const mat_res = assets.resolveMaterial(mat_guid);
                if (mat_res.render.blend != .disabled) continue; // opaque only

                // A probe's own capture never samples another probe (avoids a
                // circular bake dependency) — weight 0 leaves it unblended.
                const no_probe = draw.ProbeSample{ .tex = env.prefiltered_tex, .weight = 0, .mip_count = 1 };
                const dp = draw.buildDrawParams(&mat_res, gm, sm.index_offset, sm.index_count, vub, false, dctx, no_probe);
                draw.submitDraw(cmd, pass, dev, &bind_state, fu, &dp);
            }
        }
    }

    c.SDL_EndGPURenderPass(pass);
}

/// Capture all 6 faces of `rp` (placed at `obj`'s position) and GGX-prefilter
/// the result into `slot`, replacing whatever it held before. `slot`'s
/// key/cached-* fields are written up front (before capture), so a failed
/// prefilter still leaves the slot correctly claimed for retry next frame
/// instead of a different probe silently stealing it.
fn bakeOne(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler_default: *c.SDL_GPUSampler,
    objects: []const engine.SceneNode,
    env: BakeEnv,
    obj: *const engine.SceneNode,
    rp: *const engine.ReflectionProbeComponent,
    slot: *state.ReflectionProbeSlot,
    key: []const u8,
) void {
    const size = std.math.clamp(rp.capture_size, 32, 1024);
    if (!ensureCaptureTargets(dev, size)) return;

    const pos = obj.transform.position;
    slot.key_len = @min(key.len, state.KEY_CAP);
    @memcpy(slot.key[0..slot.key_len], key[0..slot.key_len]);
    slot.cached_pos = .{ pos.x, pos.y, pos.z };
    slot.cached_shape = @intFromEnum(rp.shape);
    slot.cached_extents_or_radius = if (rp.shape == .box)
        .{ rp.extents.x, rp.extents.y, rp.extents.z }
    else
        .{ rp.radius, 0, 0 };
    slot.cached_capture_size = size;

    for (0..6) |face| {
        captureFace(cmd, dev, sampler_default, objects, env, size, @intCast(face), pos, FACE_DIRS[face], FACE_UPS[face], rp.near, rp.far);
    }

    const prefiltered = ibl_prefilter.prefilterCubemap(cmd, dev, state.probe_capture_base.?, PROBE_PREFILTER_SIZE, PROBE_MIP_COUNT) catch |err| {
        log.warn("reflection probe bake failed for {s}: {any}", .{ key, err });
        return;
    };

    if (slot.prefiltered_cubemap) |old| c.SDL_ReleaseGPUTexture(dev, old);
    slot.prefiltered_cubemap = prefiltered;
    slot.mip_count = PROBE_MIP_COUNT;
    slot.baked = true;
    state.reflection_probes_generation +%= 1;
}

/// Force `key`'s baked slot (if any) stale, so the next `bakeDirty` call
/// rebakes it even though nothing about the probe's transform/capture
/// settings changed — the editor's "Bake Now" action.
pub fn invalidate(key: []const u8) void {
    if (state.findReflectionProbe(key)) |s| s.cached_capture_size = 0;
}

/// Bake at most one stale/new `ReflectionProbeComponent` per call — static
/// and throttled, since this runs from `renderScene` every frame but a probe
/// only actually needs rebaking when its transform/capture settings change
/// (or it's placed for the first time). Never re-baked in Play mode; nothing
/// here reads `Frame`/simulation state.
pub fn bakeDirty(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler_default: *c.SDL_GPUSampler,
    objects: []const engine.SceneNode,
    env: BakeEnv,
) void {
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .reflection_probe) continue;
            const rp = &comp.reflection_probe;
            const key = obj.guidSlice();
            if (key.len == 0) continue;

            const pos = obj.transform.position;
            const size = std.math.clamp(rp.capture_size, 32, 1024);
            const cur_pos: [3]f32 = .{ pos.x, pos.y, pos.z };
            const cur_shape: u8 = @intFromEnum(rp.shape);
            const cur_eor: [3]f32 = if (rp.shape == .box)
                .{ rp.extents.x, rp.extents.y, rp.extents.z }
            else
                .{ rp.radius, 0, 0 };

            var slot = state.findReflectionProbe(key);
            if (slot) |s| {
                if (s.baked and s.cached_capture_size == size and s.cached_shape == cur_shape and
                    std.meta.eql(s.cached_pos, cur_pos) and std.meta.eql(s.cached_extents_or_radius, cur_eor))
                {
                    continue; // already fresh
                }
            } else {
                for (&state.reflection_probes) |*s| {
                    if (s.key_len == 0) {
                        slot = s;
                        break;
                    }
                }
                if (slot == null) {
                    if (!budget_warned) {
                        log.warn("reflection probe budget exceeded ({d}) — {s} and later probes will not be baked", .{ state.MAX_REFLECTION_PROBES, key });
                        budget_warned = true;
                    }
                    continue;
                }
            }

            var z = engine.Profiler.passZone("render.probe_bake");
            defer z.end();
            bakeOne(cmd, dev, sampler_default, objects, env, obj, rp, slot.?, key);
            return; // throttle: at most one dirty probe baked per call
        }
    }
}

/// 1.0 inside the probe's shape (or always, for a global probe), falling off
/// linearly to 0 across `blend_distance` outside it — the same model
/// `postprocess.zig`'s `volumeWeight`/`boxOutsideDistance` use for post-process
/// volumes, applied here to probe influence instead.
fn probeWeightAt(rp: *const engine.ReflectionProbeComponent, t: *const engine.Transform, p: Vector3) f32 {
    if (rp.is_global) return 1.0;
    const dist = switch (rp.shape) {
        .sphere => @max(Vector3.distance(p, t.position) - rp.radius, 0.0),
        .box => boxOutsideDistance(p, t, rp.extents),
    };
    if (dist <= 0) return 1.0;
    if (rp.blend_distance <= 0) return 0.0;
    return std.math.clamp(1.0 - dist / rp.blend_distance, 0.0, 1.0);
}

/// Distance from `p` to the surface of an oriented box (0 if inside).
fn boxOutsideDistance(p: Vector3, t: *const engine.Transform, extents: Vector3) f32 {
    const inv_rot = Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z).transpose();
    const local = inv_rot.transformDirection(p.subtract(t.position));
    const qx = @abs(local.x) - extents.x;
    const qy = @abs(local.y) - extents.y;
    const qz = @abs(local.z) - extents.z;
    const ox = @max(qx, 0.0);
    const oy = @max(qy, 0.0);
    const oz = @max(qz, 0.0);
    return @sqrt(ox * ox + oy * oy + oz * oz);
}

const ProbeMatch = struct { slot_index: i32 = -1, weight: f32 = 0 };

/// The best-matching baked probe for world point `p`: highest influence
/// weight, ties broken by higher `priority`. Only considers probes that have
/// completed at least one successful bake — a placed-but-not-yet-baked probe
/// contributes nothing rather than sampling an empty texture.
fn bestProbeMatch(objects: []const engine.SceneNode, p: Vector3) ProbeMatch {
    var best = ProbeMatch{};
    if (!state.features.reflection_probes) return best;
    var best_priority: f32 = -std.math.inf(f32);

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .reflection_probe) continue;
            const rp = &comp.reflection_probe;
            const key = obj.guidSlice();
            if (key.len == 0) continue;

            const idx = state.reflectionProbeIndex(key);
            if (idx < 0) continue;
            const slot = &state.reflection_probes[@intCast(idx)];
            if (!slot.baked or slot.prefiltered_cubemap == null) continue;

            const w = probeWeightAt(rp, &obj.transform, p);
            if (w <= 0) continue;
            if (w > best.weight or (w == best.weight and rp.priority > best_priority)) {
                best = .{ .slot_index = idx, .weight = w };
                best_priority = rp.priority;
            }
        }
    }
    return best;
}

fn sampleFromMatch(m: ProbeMatch, fallback: *c.SDL_GPUTexture) draw.ProbeSample {
    if (m.slot_index < 0) return .{ .tex = fallback, .weight = 0, .mip_count = 1 };
    const slot = &state.reflection_probes[@intCast(m.slot_index)];
    return .{
        .tex = slot.prefiltered_cubemap orelse fallback,
        .weight = m.weight,
        .mip_count = @floatFromInt(slot.mip_count),
    };
}

/// The best-matching baked probe for world point `p`, or `fallback` at weight
/// 0 if none applies. Uncached — for one-off lookups; per-submesh draws
/// should use `resolveForSubmesh` instead.
pub fn resolveForPoint(objects: []const engine.SceneNode, p: Vector3, fallback: *c.SDL_GPUTexture) draw.ProbeSample {
    return sampleFromMatch(bestProbeMatch(objects, p), fallback);
}

/// As `resolveForPoint`, but resolved against `sm`'s world-space bounds
/// center and cached on `sm` behind `state.reflection_probes_generation` —
/// probes and geometry are both static, so re-testing every submesh against
/// every probe's shape every frame is wasted once nothing has rebaked.
/// Caches the winning probe's *slot index*, not its texture pointer: a
/// rebake bumps the generation (forcing a fresh resolve) and releases the
/// old texture object, so a cached pointer would risk going stale.
pub fn resolveForSubmesh(
    objects: []const engine.SceneNode,
    sm: *state.GpuSubmesh,
    model: Matrix4,
    fallback: *c.SDL_GPUTexture,
) draw.ProbeSample {
    if (sm.probe_gen != state.reflection_probes_generation) {
        const center = culling.worldAabb(sm.bounds_min, sm.bounds_max, model).center;
        const m = bestProbeMatch(objects, center);
        sm.probe_slot = m.slot_index;
        sm.probe_weight = m.weight;
        sm.probe_gen = state.reflection_probes_generation;
    }
    return sampleFromMatch(.{ .slot_index = sm.probe_slot, .weight = sm.probe_weight }, fallback);
}
