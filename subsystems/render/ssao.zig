//! Screen-space ambient occlusion: raw hemisphere-sample pass + a depth-aware
//! denoise blur, both reading the depth+normal prepass (`prepass.zig`).
//! Tunables are hardcoded constants, not authorable settings, consistent with
//! this renderer's other renderer-internal defaults (e.g. the shadow pass's bias).
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const postprocess_pipeline = @import("postprocess_pipeline.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;

const KERNEL_SIZE = 24;
const RADIUS: f32 = 0.5;
const BIAS: f32 = 0.025;
const POWER: f32 = 1.5;

var kernel: [KERNEL_SIZE][4]f32 = undefined;
var kernel_ready = false;

/// Lazily build the fixed hemisphere kernel (tangent space, mean direction
/// +Z) once. Deterministic seed — this is a baked lookup table, not
/// per-frame randomness.
fn ensureKernel() *const [KERNEL_SIZE][4]f32 {
    if (!kernel_ready) {
        var prng = std.Random.DefaultPrng.init(0x5EED_5EED);
        const rnd = prng.random();
        for (0..KERNEL_SIZE) |i| {
            var x = rnd.float(f32) * 2.0 - 1.0;
            var y = rnd.float(f32) * 2.0 - 1.0;
            var z = rnd.float(f32);
            const len = @sqrt(x * x + y * y + z * z);
            if (len > 1e-6) {
                x /= len;
                y /= len;
                z /= len;
            }
            const s = rnd.float(f32);
            x *= s;
            y *= s;
            z *= s;
            // Push more samples toward the origin so nearby occluders (the
            // ones that matter most for contact darkening) get denser coverage.
            var scale: f32 = @as(f32, @floatFromInt(i)) / @as(f32, KERNEL_SIZE);
            scale = std.math.lerp(@as(f32, 0.1), @as(f32, 1.0), scale * scale);
            kernel[i] = .{ x * scale, y * scale, z * scale, 0 };
        }
        kernel_ready = true;
    }
    return &kernel;
}

/// Both SSAO sub-pass pipelines read 2 fragment samplers, sample count 1
/// (never MSAA), output single-channel `SSAO_FORMAT`, no depth attachment.
pub fn createSsaoPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/ssao.frag.spv");
    return postprocess_pipeline.buildPipeline(dev, vert_spv, frag_spv, 2, 1, state.SSAO_FORMAT, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}

pub fn createSsaoBlurPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/fullscreen.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/ssao_blur.frag.spv");
    return postprocess_pipeline.buildPipeline(dev, vert_spv, frag_spv, 2, 0, state.SSAO_FORMAT, std.mem.zeroes(c.SDL_GPUColorTargetBlendState));
}

fn createSsaoTex(dev: *c.SDL_GPUDevice, w: u32, h: u32) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = state.SSAO_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.SsaoTexCreate;
}

fn makeTarget(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?state.SsaoTarget {
    const raw = createSsaoTex(dev, w, h) catch return null;
    const blurred = createSsaoTex(dev, w, h) catch {
        c.SDL_ReleaseGPUTexture(dev, raw);
        return null;
    };
    return .{ .raw = raw, .blurred = blurred, .w = w, .h = h };
}

fn releaseTarget(dev: *c.SDL_GPUDevice, t: *state.SsaoTarget) void {
    if (t.raw) |r| c.SDL_ReleaseGPUTexture(dev, r);
    if (t.blurred) |b| c.SDL_ReleaseGPUTexture(dev, b);
    t.* = .{};
}

fn targetFor(dev: *c.SDL_GPUDevice, w: u32, h: u32) ?*state.SsaoTarget {
    if (state.findSsaoTarget(w, h)) |t| return t;

    for (&state.ssao_targets) |*t| {
        if (t.raw == null) {
            t.* = makeTarget(dev, w, h) orelse return null;
            return t;
        }
    }

    const t = &state.ssao_targets[state.ssao_evict_cursor];
    state.ssao_evict_cursor = (state.ssao_evict_cursor + 1) % state.ssao_targets.len;
    releaseTarget(dev, t);
    t.* = makeTarget(dev, w, h) orelse return null;
    return t;
}

/// Release every cached SSAO target (called from `root.zig:deinit`).
pub fn destroyTargets(dev: *c.SDL_GPUDevice) void {
    for (&state.ssao_targets) |*t| releaseTarget(dev, t);
    state.ssao_evict_cursor = 0;
}

/// Run the raw SSAO pass then its denoise blur, reading `prepass_target`.
/// `sampler` is a clamp-to-edge sampler (screen-space reads must not wrap at
/// the viewport edge) — the renderer reuses `state.cubemap_sampler` for this.
pub fn run(
    cmd: *c.SDL_GPUCommandBuffer,
    dev: *c.SDL_GPUDevice,
    sampler: *c.SDL_GPUSampler,
    w: u32,
    h: u32,
    prepass_target: *const state.PrepassTarget,
    proj: Matrix4,
) ?*c.SDL_GPUTexture {
    const raw_pl = state.ssao_pipeline orelse return null;
    const blur_pl = state.ssao_blur_pipeline orelse return null;
    const depth_tex = prepass_target.depth orelse return null;
    const normal_tex = prepass_target.normal orelse return null;
    // Half-res AO is sampled through a clamped linear sampler by screen UV, so
    // only the target and the viewport shrink; every consumer is unchanged.
    const sw = if (state.features.ssao_half_res) @max(1, w / 2) else w;
    const sh = if (state.features.ssao_half_res) @max(1, h / 2) else h;
    const target = targetFor(dev, sw, sh) orelse return null;

    var zone = engine.Profiler.passZone("render.ssao");
    defer zone.end();

    // Fewer taps reuse a prefix of the same kernel, so the sample distribution
    // stays the one `ensureKernel` built rather than being regenerated per count.
    const taps = @max(1, @min(state.features.ssao_samples, KERNEL_SIZE));

    const ub = types.SsaoUB{
        .inv_proj = proj.inverse().m,
        .proj = proj.m,
        .kernel_samples = ensureKernel().*,
        .params = .{ RADIUS, BIAS, POWER, @floatFromInt(taps) },
    };
    postprocess_pipeline.fullscreenPass(cmd, raw_pl, target.raw.?, sw, sh, &[_]c.SDL_GPUTextureSamplerBinding{
        .{ .texture = depth_tex, .sampler = sampler },
        .{ .texture = normal_tex, .sampler = sampler },
    }, &ub, @sizeOf(types.SsaoUB));

    postprocess_pipeline.fullscreenPass(cmd, blur_pl, target.blurred.?, sw, sh, &[_]c.SDL_GPUTextureSamplerBinding{
        .{ .texture = target.raw.?, .sampler = sampler },
        .{ .texture = depth_tex, .sampler = sampler },
    }, null, 0);

    return target.blurred;
}
