//! Per-frame post-process orchestration: the bloom mip-chain build
//! (threshold -> downsample -> upsample) and the final composite pass that
//! adds bloom, applies per-channel Lift/Gamma/Gain grading and vignette, then
//! ACES tonemaps into the caller's UNORM `color_tex`. GPU resource lifecycle
//! (pipelines, textures) lives in `postprocess_pipeline.zig`/`state.zig`.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const postprocess_pipeline = @import("postprocess_pipeline.zig");

const c = gpu.c;

/// Camera post-process parameters, resolved by `resolveVolumes` from every
/// active `PostProcessVolumeComponent` affecting the camera (or left at these
/// all-neutral defaults, a hard no-op, when none apply).
pub const PostProcessSettings = struct {
    vignette_intensity: f32 = 0,
    vignette_radius: f32 = 0.75,
    vignette_smoothness: f32 = 0.35,
    grading_lift: [3]f32 = .{ 0, 0, 0 },
    grading_gamma: [3]f32 = .{ 1, 1, 1 },
    grading_gain: [3]f32 = .{ 1, 1, 1 },
    bloom_threshold: f32 = 1.0,
    bloom_intensity: f32 = 0,
    bloom_radius: f32 = 1.0,
};

/// Cap on volumes blended together per frame.
pub const MAX_VOLUMES = 32;

const VolumeCandidate = struct {
    v: *const engine.PostProcessVolumeComponent,
    weight: f32,
};

/// Resolve blended post-process settings affecting `cam_pos` from every active
/// `PostProcessVolumeComponent` in `objects`. Collected volumes are sorted
/// ascending by priority, then blended per-category in sequential lerp order:
/// `result = lerp(result, volume, weight)` — a higher-priority volume at
/// weight 1 fully overrides everything blended before it. A volume only
/// participates in a category if that category's own `enabled` is true.
pub fn resolveVolumes(cam_pos: engine.Vector3, objects: []const engine.SceneNode) PostProcessSettings {
    var cand: [MAX_VOLUMES]VolumeCandidate = undefined;
    var n: usize = 0;
    for (objects) |*obj| {
        if (!obj.active or n >= MAX_VOLUMES) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .post_process_volume) continue;
            const pv = &comp.post_process_volume;
            const w = volumeWeight(pv, &obj.transform, cam_pos);
            if (w <= 0) continue;
            cand[n] = .{ .v = pv, .weight = w };
            n += 1;
        }
    }

    // Insertion sort ascending by priority — n is small (<= MAX_VOLUMES),
    // not worth pulling in std.sort for this.
    var i: usize = 1;
    while (i < n) : (i += 1) {
        const key = cand[i];
        var j = i;
        while (j > 0 and cand[j - 1].v.priority > key.v.priority) : (j -= 1) cand[j] = cand[j - 1];
        cand[j] = key;
    }

    var result = PostProcessSettings{};
    for (cand[0..n]) |cd| {
        if (cd.v.vignette.enabled) blendVignette(&result, cd.v.vignette, cd.weight);
        if (cd.v.grading.enabled) blendGrading(&result, cd.v.grading, cd.weight);
        if (cd.v.bloom.enabled) blendBloom(&result, cd.v.bloom, cd.weight);
    }
    return result;
}

/// 1.0 inside the volume's shape (or always, for a global volume), falling
/// off linearly to 0 across `blend_distance` outside it.
fn volumeWeight(pv: *const engine.PostProcessVolumeComponent, t: *const engine.Transform, cam_pos: engine.Vector3) f32 {
    if (pv.is_global) return 1.0;
    const dist = switch (pv.shape) {
        .sphere => @max(engine.Vector3.distance(cam_pos, t.position) - pv.radius, 0.0),
        .box => boxOutsideDistance(cam_pos, t, pv.extents),
    };
    if (dist <= 0) return 1.0;
    if (pv.blend_distance <= 0) return 0.0;
    return std.math.clamp(1.0 - dist / pv.blend_distance, 0.0, 1.0);
}

/// Distance from `p` to the surface of an oriented box (0 if inside). Inverse-rotates
/// `p` into the box's local space (the transpose of a pure rotation matrix
/// is its inverse), then evaluates a standard box signed-distance function.
fn boxOutsideDistance(p: engine.Vector3, t: *const engine.Transform, extents: engine.Vector3) f32 {
    const inv_rot = engine.Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z).transpose();
    const local = inv_rot.transformDirection(p.subtract(t.position));
    const qx = @abs(local.x) - extents.x;
    const qy = @abs(local.y) - extents.y;
    const qz = @abs(local.z) - extents.z;
    const ox = @max(qx, 0.0);
    const oy = @max(qy, 0.0);
    const oz = @max(qz, 0.0);
    return @sqrt(ox * ox + oy * oy + oz * oz);
}

fn lerp(a: f32, b: f32, t: f32) f32 {
    return a + (b - a) * t;
}

fn blendVignette(r: *PostProcessSettings, s: engine.VignetteSettings, w: f32) void {
    r.vignette_intensity = lerp(r.vignette_intensity, s.intensity, w);
    r.vignette_radius = lerp(r.vignette_radius, s.radius, w);
    r.vignette_smoothness = lerp(r.vignette_smoothness, s.smoothness, w);
}

fn blendGrading(r: *PostProcessSettings, s: engine.ColorGradingSettings, w: f32) void {
    r.grading_lift = .{ lerp(r.grading_lift[0], s.lift.x, w), lerp(r.grading_lift[1], s.lift.y, w), lerp(r.grading_lift[2], s.lift.z, w) };
    r.grading_gamma = .{ lerp(r.grading_gamma[0], s.gamma.x, w), lerp(r.grading_gamma[1], s.gamma.y, w), lerp(r.grading_gamma[2], s.gamma.z, w) };
    r.grading_gain = .{ lerp(r.grading_gain[0], s.gain.x, w), lerp(r.grading_gain[1], s.gain.y, w), lerp(r.grading_gain[2], s.gain.z, w) };
}

fn blendBloom(r: *PostProcessSettings, s: engine.BloomSettings, w: f32) void {
    r.bloom_threshold = lerp(r.bloom_threshold, s.threshold, w);
    r.bloom_intensity = lerp(r.bloom_intensity, s.intensity, w);
    r.bloom_radius = lerp(r.bloom_radius, s.radius, w);
}

/// A user-registered custom post-process effect, run in registration order
/// after bloom generation but before the composite pass — in HDR space, so
/// effects can read/produce values outside the 0..1 display range.
/// `src`/`dst` are always distinct textures (never assume in-place).
pub const CustomEffect = struct {
    ctx: ?*anyopaque,
    runFn: *const fn (ctx: ?*anyopaque, cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, sampler: *c.SDL_GPUSampler, src: *c.SDL_GPUTexture, dst: *c.SDL_GPUTexture, w: u32, h: u32) void,
};
const MAX_CUSTOM_EFFECTS = 8;
var custom_effects: [MAX_CUSTOM_EFFECTS]?CustomEffect = .{null} ** MAX_CUSTOM_EFFECTS;
var custom_effect_count: usize = 0;

/// Register a custom effect to run every frame, in registration order, after
/// bloom generation and before the composite pass.
pub fn registerEffect(effect: CustomEffect) void {
    if (custom_effect_count >= MAX_CUSTOM_EFFECTS) return;
    custom_effects[custom_effect_count] = effect;
    custom_effect_count += 1;
}

/// Drop every registered custom effect.
pub fn clearEffects() void {
    custom_effect_count = 0;
}

/// The scratch pair cached for `w`x`h`, building it (and evicting the oldest
/// cached pair, round-robin) on first use.
fn getOrCreateScratchPair(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*state.ScratchPair {
    if (state.findScratchPair(w, h)) |sp| return sp;

    const slot = &state.scratch_targets[state.scratch_evict_cursor];
    state.scratch_evict_cursor = (state.scratch_evict_cursor + 1) % state.MAX_SCRATCH_TARGETS;
    if (slot.a) |t| c.SDL_ReleaseGPUTexture(dev, t);
    if (slot.b) |t| c.SDL_ReleaseGPUTexture(dev, t);
    slot.* = .{};

    slot.a = try postprocess_pipeline.createHdrColor(dev, w, h);
    slot.b = try postprocess_pipeline.createHdrColor(dev, w, h);
    slot.w = w;
    slot.h = h;
    return slot;
}

/// Run every registered custom effect in order, ping-ponging between two HDR
/// scratch textures. Returns `hdr_color` unchanged if nothing is registered.
fn runCustomEffects(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler,
    hdr_color: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
) *c.SDL_GPUTexture {
    if (custom_effect_count == 0) return hdr_color;
    const pair = getOrCreateScratchPair(dev, w, h) catch return hdr_color;

    var src = hdr_color;
    var dst = pair.a.?;
    var i: usize = 0;
    while (i < custom_effect_count) : (i += 1) {
        const eff = custom_effects[i].?;
        eff.runFn(eff.ctx, cmd, dev, sampler, src, dst, w, h);
        src = dst;
        dst = if (dst == pair.a.?) pair.b.? else pair.a.?;
    }
    return src;
}

/// Mip dimensions below this stop the downsample chain early on small viewports.
const MIN_BLOOM_MIP = 16;

/// How many halving-resolution mips fit between mip0 (half of `w`x`h`) and
/// `MIN_BLOOM_MIP`, capped at `state.MAX_BLOOM_MIPS`.
fn bloomMipCount(w: u32, h: u32) usize {
    var mw = @max(w / 2, 1);
    var mh = @max(h / 2, 1);
    var count: usize = 1;
    while (count < state.MAX_BLOOM_MIPS) {
        const nw = mw / 2;
        const nh = mh / 2;
        if (nw < MIN_BLOOM_MIP or nh < MIN_BLOOM_MIP) break;
        mw = nw;
        mh = nh;
        count += 1;
    }
    return count;
}

/// The bloom chain cached for `w`x`h`, built (and evicting the oldest
/// chain, round-robin) on first use.
fn getOrCreateBloomChain(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*state.BloomChain {
    if (state.findBloomChain(w, h)) |bc| return bc;

    const mip_count = bloomMipCount(w, h);
    const slot = &state.bloom_targets[state.bloom_evict_cursor];
    state.bloom_evict_cursor = (state.bloom_evict_cursor + 1) % state.MAX_BLOOM_TARGETS;

    for (slot.mips[0..slot.mip_count]) |mip| {
        if (mip) |t| c.SDL_ReleaseGPUTexture(dev, t);
    }
    slot.* = .{};

    var mw = @max(w / 2, 1);
    var mh = @max(h / 2, 1);
    var i: usize = 0;
    while (i < mip_count) : (i += 1) {
        slot.mips[i] = postprocess_pipeline.createBloomMip(dev, mw, mh) catch |err| {
            slot.mip_count = i;
            return err;
        };
        slot.mip_w[i] = mw;
        slot.mip_h[i] = mh;
        mw = @max(mw / 2, 1);
        mh = @max(mh / 2, 1);
    }
    slot.mip_count = mip_count;
    slot.w = w;
    slot.h = h;
    return slot;
}

/// Draw one fullscreen triangle into `dst`, sampling `src_tex` at slot 0 and
/// optionally pushing a fragment uniform. Shared by every bloom sub-pass.
fn fullscreenPass(
    cmd: *c.SDL_GPUCommandBuffer,
    pl: *c.SDL_GPUGraphicsPipeline,
    dst: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    load_op: c.SDL_GPULoadOp,
    src_tex: *c.SDL_GPUTexture,
    sampler: *c.SDL_GPUSampler,
    ub: ?*const anyopaque,
    ub_size: u32,
) void {
    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = dst;
    color_info.load_op = load_op;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, null) orelse return;
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    const binding = c.SDL_GPUTextureSamplerBinding{ .texture = src_tex, .sampler = sampler };
    c.SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
    if (ub) |ptr| c.SDL_PushGPUFragmentUniformData(cmd, 0, ptr, ub_size);
    c.SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
    c.SDL_EndGPURenderPass(pass);
}

/// Build the bloom mip chain and return the combined result (mip0), or null
/// if bloom is off (`bloom_intensity <= 0`) or its pipelines aren't ready.
fn runBloom(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler,
    hdr_color: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    settings: PostProcessSettings,
) ?*c.SDL_GPUTexture {
    if (settings.bloom_intensity <= 0) return null;
    const threshold_p = state.post_threshold_pipeline orelse return null;
    const downsample_p = state.post_downsample_pipeline orelse return null;
    const upsample_p = state.post_upsample_pipeline orelse return null;

    const chain = getOrCreateBloomChain(dev, w, h) catch return null;
    if (chain.mip_count == 0) return null;

    const threshold_ub = types.PostBloomUB{ .params = .{ settings.bloom_threshold, @max(settings.bloom_threshold * 0.5, 0.0001), 0, 0 } };
    fullscreenPass(cmd, threshold_p, chain.mips[0].?, chain.mip_w[0], chain.mip_h[0], c.SDL_GPU_LOADOP_DONT_CARE, hdr_color, sampler, &threshold_ub, @sizeOf(types.PostBloomUB));

    var i: usize = 0;
    while (i + 1 < chain.mip_count) : (i += 1) {
        fullscreenPass(cmd, downsample_p, chain.mips[i + 1].?, chain.mip_w[i + 1], chain.mip_h[i + 1], c.SDL_GPU_LOADOP_DONT_CARE, chain.mips[i].?, sampler, null, 0);
    }

    const upsample_ub = types.PostBloomUB{ .params = .{ 0, 0, settings.bloom_radius, 0 } };
    i = chain.mip_count - 1;
    while (i > 0) : (i -= 1) {
        fullscreenPass(cmd, upsample_p, chain.mips[i - 1].?, chain.mip_w[i - 1], chain.mip_h[i - 1], c.SDL_GPU_LOADOP_LOAD, chain.mips[i].?, sampler, &upsample_ub, @sizeOf(types.PostBloomUB));
    }

    return chain.mips[0];
}

/// Final combine: bloom add, per-channel grading, vignette, ACES tonemap,
/// gamma encode. Writes the caller's UNORM `color_tex`.
fn runComposite(
    cmd: *c.SDL_GPUCommandBuffer,
    pl: *c.SDL_GPUGraphicsPipeline,
    sampler: *c.SDL_GPUSampler,
    hdr_color: *c.SDL_GPUTexture,
    bloom_tex: *c.SDL_GPUTexture,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    settings: PostProcessSettings,
) void {
    const aspect = @as(f32, @floatFromInt(w)) / @as(f32, @floatFromInt(@max(h, 1)));
    const ub = types.PostCompositeUB{
        .vignette = .{ settings.vignette_intensity, settings.vignette_radius, settings.vignette_smoothness, aspect },
        .lift = .{ settings.grading_lift[0], settings.grading_lift[1], settings.grading_lift[2], 0 },
        .gamma = .{ settings.grading_gamma[0], settings.grading_gamma[1], settings.grading_gamma[2], 0 },
        .gain = .{ settings.grading_gain[0], settings.grading_gain[1], settings.grading_gain[2], 0 },
        .bloom = .{ settings.bloom_intensity, 0, 0, 0 },
    };

    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = color_tex;
    color_info.load_op = c.SDL_GPU_LOADOP_DONT_CARE;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, null) orelse return;
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    const bindings = [_]c.SDL_GPUTextureSamplerBinding{
        .{ .texture = hdr_color, .sampler = sampler },
        .{ .texture = bloom_tex, .sampler = sampler },
    };
    c.SDL_BindGPUFragmentSamplers(pass, 0, &bindings, 2);
    c.SDL_PushGPUFragmentUniformData(cmd, 0, &ub, @sizeOf(types.PostCompositeUB));
    c.SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
    c.SDL_EndGPURenderPass(pass);
}

/// Run the post-process stack: bloom (if enabled) then the composite pass,
/// reading `hdr_color` and writing `color_tex`. A no-op if the composite
/// pipeline or its black-texture fallback failed to create.
pub fn run(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler,
    hdr_color: *c.SDL_GPUTexture,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    settings: PostProcessSettings,
) void {
    var zone = engine.Profiler.passZone("render.postprocess");
    defer zone.end();

    const composite_p = state.post_composite_pipeline orelse return;
    const black = state.black_tex orelse return;
    // Bloom reads from the original hdr_color, not from the custom-effect chain.
    const bloom_tex = runBloom(cmd, dev, sampler, hdr_color, w, h, settings) orelse black;
    const final_hdr = runCustomEffects(cmd, dev, sampler, hdr_color, w, h);
    runComposite(cmd, composite_p, sampler, final_hdr, bloom_tex, color_tex, w, h, settings);
}
