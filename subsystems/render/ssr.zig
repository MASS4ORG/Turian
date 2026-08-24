//! Screen-space reflections: a fullscreen raymarch over the depth+normal
//! prepass (`prepass.zig`), reprojecting hits into last frame's composited
//! color — see `run`'s doc comment. Tunables are hardcoded constants, not
//! authorable settings, matching this renderer's other internal defaults.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const postprocess_pipeline = @import("postprocess_pipeline.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;

const THICKNESS: f32 = 0.2; // view-space meters — intersection tolerance
const STEP_SCALE: f32 = 0.05; // fraction of |view-space depth| per march step
const MAX_DISTANCE: f32 = 20.0; // view-space meters — ray reach cap

/// SSR pipeline: 3 fragment samplers (prepass depth, prepass normal, last
/// frame's `hdr_color`), 1 uniform buffer, single-sample, output format
/// matches `HDR_COLOR_FORMAT` — the result carries actual scene radiance
/// (plus a confidence alpha), not a scalar mask like SSAO's `R8_UNORM`.
pub fn createSsrPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/ssr.frag.spv");
    return postprocess_pipeline.buildPipeline(dev, vert_spv, frag_spv, 3, 1, state.HDR_COLOR_FORMAT, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}

fn createSsrTex(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.HDR_COLOR_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.SsrTexCreate;
}

fn releaseTarget(dev: *c.SDL_GPUDevice, t: *state.SsrTarget) void {
    if (t.tex) |tex| c.SDL_ReleaseGPUTexture(dev, tex);
    t.* = .{};
}

fn targetFor(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?*state.SsrTarget {
    if (state.findSsrTarget(w, h)) |t| return t;

    for (&state.ssr_targets) |*t| {
        if (t.tex == null) {
            t.tex = createSsrTex(dev, w, h) catch return null;
            t.w = w;
            t.h = h;
            return t;
        }
    }

    const t = &state.ssr_targets[state.ssr_evict_cursor];
    state.ssr_evict_cursor = (state.ssr_evict_cursor + 1) % state.ssr_targets.len;
    releaseTarget(dev, t);
    t.tex = createSsrTex(dev, w, h) catch return null;
    t.w = w;
    t.h = h;
    return t;
}

/// Release every cached SSR target (called from `root.zig:deinit`).
pub fn destroyTargets(dev: *c.SDL_GPUDevice) void {
    for (&state.ssr_targets) |*t| releaseTarget(dev, t);
    state.ssr_evict_cursor = 0;
}

/// Run the SSR raymarch, or return null if unavailable: pipeline/prepass
/// missing, or no valid previous frame to reproject into (first frame ever,
/// a resize, or a dropped/duplicated `frame_seq`). `history_color` is this
/// frame's `target.hdr_color` — obtained *before* the main pass's
/// `LOADOP_CLEAR` overwrites it, so it still holds last frame's composite.
pub fn run(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler, // clamp-to-edge (state.cubemap_sampler)
    w: u32,
    h: u32,
    prepass_target: *const state.PrepassTarget,
    view: Matrix4,
    proj: Matrix4,
    history_color: *c.SDL_GPUTexture,
) ?*c.SDL_GPUTexture {
    const pl = state.ssr_pipeline orelse return null;
    const depth_tex = prepass_target.depth orelse return null;
    const normal_tex = prepass_target.normal orelse return null;

    const valid = state.prev_frame_seq != 0 and
        state.frame_seq == state.prev_frame_seq + 1 and
        state.prev_w == w and state.prev_h == h;
    if (!valid) return null;

    const target = targetFor(dev, w, h) orelse return null;

    var zone = engine.Profiler.passZone("render.ssr");
    defer zone.end();

    const reproject = state.prev_view_proj.multiply(view.inverse());
    const ub = types.SsrUB{
        .inv_proj = proj.inverse().m,
        .proj = proj.m,
        .reproject = reproject.m,
        .params = .{ THICKNESS, STEP_SCALE, MAX_DISTANCE, 0 },
    };

    postprocess_pipeline.fullscreenPass(cmd, pl, target.tex.?, w, h, &[_]c.SDL_GPUTextureSamplerBinding{
        .{ .texture = depth_tex, .sampler = sampler },
        .{ .texture = normal_tex, .sampler = sampler },
        .{ .texture = history_color, .sampler = sampler },
    }, &ub, @sizeOf(types.SsrUB));

    return target.tex;
}
