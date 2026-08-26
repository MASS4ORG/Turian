//! Shared GPU state for the renderer (global singleton — one renderer per
//! process). Other render files read/write this; the public API lives in
//! `root.zig`.
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");

const c = gpu.c;

pub const SHADOW_FORMAT = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;

/// Depth+normal prepass formats. Depth reuses `SHADOW_FORMAT`
/// (D16_UNORM), the same sampler-usable depth format `createShadowMap` already
/// proves works on this device; the prepass is always single-sample regardless
/// of the main pass's MSAA setting, so it never needs the MSAA depth's format.
pub const PREPASS_DEPTH_FORMAT = SHADOW_FORMAT;
pub const PREPASS_NORMAL_FORMAT = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
pub const SSAO_FORMAT = c.SDL_GPU_TEXTUREFORMAT_R8_UNORM;

/// HDR intermediate color format the main scene pass renders into, letting the
/// post-process composite pass work with unclamped linear values before
/// tonemapping (moved out of the lit shaders, see `postprocess.zig`).
pub const HDR_COLOR_FORMAT = c.SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;

pub var device: ?*c.SDL_GPUDevice = null;
pub var sampler: ?*c.SDL_GPUSampler = null;

/// Multisample count for the scene pass (`SDL_GPU_SAMPLECOUNT_*`). Chosen at
/// init from device support; `SAMPLECOUNT_1` disables MSAA (direct render, no
/// resolve). Read by pipeline creation and the main/gizmo render passes.
pub var sample_count: c.SDL_GPUSampleCount = c.SDL_GPU_SAMPLECOUNT_1;

/// Whether MSAA is active (sample count > 1), i.e. the scene renders into a
/// multisampled target and resolves into the caller's single-sample texture.
pub fn msaa() bool {
    return sample_count != c.SDL_GPU_SAMPLECOUNT_1;
}

/// Fixed-function state for a scene pipeline permutation.
pub const ScenePipelineState = struct {
    blend: engine.Material.BlendMode = .disabled,
    cull: engine.Material.CullMode = .back,
    depth_write: bool = true,
    depth_test: bool = true,
};

/// Scene pipelines cached by state combo, created lazily.
pub const MAX_SCENE_PIPELINES = 16;
pub const ScenePipelineEntry = struct {
    key: ScenePipelineState = .{},
    pipeline: ?*c.SDL_GPUGraphicsPipeline = null,
};
pub var scene_pipelines: [MAX_SCENE_PIPELINES]ScenePipelineEntry = .{ScenePipelineEntry{}} ** MAX_SCENE_PIPELINES;
pub var scene_pipeline_count: usize = 0;

pub var shadow_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
/// Alpha-cutout variant of `shadow_pipeline`, used for masked materials. Null
/// when its creation failed — such materials then cast a solid shadow.
pub var shadow_mask_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var shadow_map: ?*c.SDL_GPUTexture = null;
pub var shadow_sampler: ?*c.SDL_GPUSampler = null;

pub var skybox_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

/// Depth+normal prepass and SSAO pipelines. Null if creation failed —
/// `renderScene` falls back to `white_tex` for the AO input in that case.
pub var prepass_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var ssao_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var ssao_blur_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

/// Screen-space reflection pipeline. Null if creation failed — `renderScene`
/// falls back to `ssr_fallback_tex` (fully transparent) in that case, which
/// reads as "always miss" and leaves `scene.frag.glsl` on its `env_prefiltered`
/// cubemap fallback.
pub var ssr_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

/// One-time IBL specular-prefilter pipelines (see `ibl_prefilter.zig`):
/// equirect->cubemap conversion, then GGX-importance-sampled mip prefiltering.
/// Only run when a new environment texture uploads, not per frame.
pub var ibl_equirect_to_cubemap_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var ibl_prefilter_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var cubemap_sampler: ?*c.SDL_GPUSampler = null;

/// GPU-driven frustum-culling compute pipeline.
pub var cull_pipeline: ?*c.SDL_GPUComputePipeline = null;

/// Per-frame scene lights, uploaded to a graphics storage buffer the scene
/// fragment shader reads (bound once per frame, not pushed per draw). Sized for
/// `types.MAX_LIGHTS`; the active count travels in the frag uniform's
/// `camera_pos.w`. `lights_transfer` is the reused upload staging buffer.
pub var lights_buf: ?*c.SDL_GPUBuffer = null;
pub var lights_transfer: ?*c.SDL_GPUTransferBuffer = null;

pub var white_tex: ?*c.SDL_GPUTexture = null;
/// Default tangent-space "flat" normal (points straight out): rgb (128,128,255).
pub var flat_normal_tex: ?*c.SDL_GPUTexture = null;
/// Fallback prefiltered-specular-cubemap binding for scenes with no
/// environment (or before its prefiltering has run) — black, so unlit specular
/// IBL reads as "off" rather than a stray flat reflection.
pub var black_cubemap: ?*c.SDL_GPUTexture = null;

/// Render targets cached by (w,h) so no attachment is released while a pass in
/// the current frame still references it. `tex` is the depth target (MSAA when
/// `sample_count > 1`); `msaa_color` is the multisampled color the scene pass
/// resolves from (null in the single-sample path).
pub const MAX_DEPTH_TARGETS = 6;
pub const DepthTarget = struct {
    tex: ?*c.SDL_GPUTexture = null,
    msaa_color: ?*c.SDL_GPUTexture = null,
    /// Single-sample HDR resolve target the main pass renders into (or
    /// resolves into, under MSAA); the post-process composite pass reads this
    /// and writes the caller's UNORM `color_tex`.
    hdr_color: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var depth_targets: [MAX_DEPTH_TARGETS]DepthTarget = .{DepthTarget{}} ** MAX_DEPTH_TARGETS;
/// Round-robin eviction cursor, used only once every slot is occupied.
pub var depth_evict_cursor: usize = 0;

/// The cached depth texture matching `w`x`h`, or null if none is allocated yet.
pub fn findDepth(w: u32, h: u32) ?*c.SDL_GPUTexture {
    for (&depth_targets) |*d| {
        if (d.tex != null and d.w == w and d.h == h) return d.tex;
    }
    return null;
}

/// The cached render-target entry matching `w`x`h` (depth + MSAA color), or null.
pub fn findTarget(w: u32, h: u32) ?*DepthTarget {
    for (&depth_targets) |*d| {
        if (d.tex != null and d.w == w and d.h == h) return d;
    }
    return null;
}

/// Depth+normal prepass render targets, cached by (w,h) like `depth_targets`.
/// Always single-sample regardless of the main pass's MSAA setting — SSAO
/// reads these as plain sampled textures.
pub const MAX_PREPASS_TARGETS = 6;
pub const PrepassTarget = struct {
    depth: ?*c.SDL_GPUTexture = null,
    normal: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var prepass_targets: [MAX_PREPASS_TARGETS]PrepassTarget = .{PrepassTarget{}} ** MAX_PREPASS_TARGETS;
pub var prepass_evict_cursor: usize = 0;

/// The cached prepass target matching `w`x`h`, or null if none is allocated yet.
pub fn findPrepassTarget(w: u32, h: u32) ?*PrepassTarget {
    for (&prepass_targets) |*p| {
        if (p.depth != null and p.w == w and p.h == h) return p;
    }
    return null;
}

/// Raw + blurred SSAO textures, cached by (w,h).
pub const MAX_SSAO_TARGETS = 3;
pub const SsaoTarget = struct {
    raw: ?*c.SDL_GPUTexture = null,
    blurred: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var ssao_targets: [MAX_SSAO_TARGETS]SsaoTarget = .{SsaoTarget{}} ** MAX_SSAO_TARGETS;
pub var ssao_evict_cursor: usize = 0;

/// The cached SSAO target matching `w`x`h`, or null if none is allocated yet.
pub fn findSsaoTarget(w: u32, h: u32) ?*SsaoTarget {
    for (&ssao_targets) |*s| {
        if (s.raw != null and s.w == w and s.h == h) return s;
    }
    return null;
}

/// SSR result, cached by (w,h). One texture: RGB = reflected color sampled
/// from last frame's composite, A = hit confidence. `HDR_COLOR_FORMAT` (not
/// SSAO's R8_UNORM) since this carries actual scene radiance, not a scalar.
pub const MAX_SSR_TARGETS = 3;
pub const SsrTarget = struct {
    tex: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var ssr_targets: [MAX_SSR_TARGETS]SsrTarget = .{SsrTarget{}} ** MAX_SSR_TARGETS;
pub var ssr_evict_cursor: usize = 0;

/// The cached SSR target matching `w`x`h`, or null if none is allocated yet.
pub fn findSsrTarget(w: u32, h: u32) ?*SsrTarget {
    for (&ssr_targets) |*s| {
        if (s.tex != null and s.w == w and s.h == h) return s;
    }
    return null;
}

/// 1x1 fully-transparent (alpha=0) fallback bound as `ssr_tex` when SSR is
/// unavailable or its temporal reprojection is invalid this frame. Distinct
/// from `black_tex` (opaque, used by bloom) — an opaque-black SSR fallback
/// would read as a confident black reflection and darken every rough surface.
pub var ssr_fallback_tex: ?*c.SDL_GPUTexture = null;

/// Temporal-reprojection bookkeeping for SSR, updated once at the end of
/// `renderScene`. `prev_frame_seq == 0` is the "no previous frame" sentinel —
/// `frame_seq` is always >= 1, so 0 can never be a legitimate previous value.
pub var prev_view_proj: engine.Matrix4 = .{};
pub var prev_frame_seq: u64 = 0;
pub var prev_w: u32 = 0;
pub var prev_h: u32 = 0;

/// Persistent, per-placed-probe GPU resources for `reflection_probes.zig`.
/// Unlike `SsrTarget`/`SsaoTarget` (transient, evicted, keyed by resolution),
/// these are keyed by the owning `SceneNode`'s stable GUID and never evicted
/// while that node exists — probes are baked once and read every frame.
pub const MAX_REFLECTION_PROBES = 8;

pub const ReflectionProbeSlot = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    baked: bool = false,
    prefiltered_cubemap: ?*c.SDL_GPUTexture = null,
    mip_count: u32 = 0,
    /// Cached bake inputs — if any differ from the live component next
    /// frame, the slot is stale and eligible for a rebake in `bakeDirty`.
    cached_pos: [3]f32 = .{ 0, 0, 0 },
    cached_shape: u8 = 0,
    cached_extents_or_radius: [3]f32 = .{ 0, 0, 0 },
    cached_capture_size: u32 = 0,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var reflection_probes: [MAX_REFLECTION_PROBES]ReflectionProbeSlot = .{ReflectionProbeSlot{}} ** MAX_REFLECTION_PROBES;
/// Bumped whenever any probe (re)bakes — invalidates `GpuSubmesh`'s cached
/// probe assignment (`probe_gen`) without a full rescan every frame.
pub var reflection_probes_generation: u32 = 0;

/// The reflection-probe slot whose `key` matches `k`, or null if unbaked/unplaced.
pub fn findReflectionProbe(k: []const u8) ?*ReflectionProbeSlot {
    for (&reflection_probes) |*p| {
        if (p.key_len != 0 and p.matchesKey(k)) return p;
    }
    return null;
}

/// Index into `reflection_probes` of the slot whose `key` matches `k`, or -1.
/// Callers cache this (not a raw texture pointer) since a rebake releases and
/// recreates the slot's GPU texture object — see `GpuSubmesh.probe_slot`.
pub fn reflectionProbeIndex(k: []const u8) i32 {
    for (&reflection_probes, 0..) |*p, i| {
        if (p.key_len != 0 and p.matchesKey(k)) return @intCast(i);
    }
    return -1;
}

/// Scratch capture targets for probe baking, pooled by resolution — probes
/// bake one face at a time, sequentially, so a single reusable pair bounds
/// VRAM regardless of `MAX_REFLECTION_PROBES`. `probe_capture_depth` is
/// deliberately a plain 2D texture, reused per-face within one probe's bake:
/// SDL_GPU disallows `DEPTH_STENCIL_TARGET` on array textures (the same
/// constraint the cascaded shadow atlas works around, see ADR-0010), so only
/// the captured *color* cubemap may be a cube/array texture.
pub var probe_capture_base: ?*c.SDL_GPUTexture = null;
pub var probe_capture_base_size: u32 = 0;
pub var probe_capture_depth: ?*c.SDL_GPUTexture = null;
pub var probe_capture_depth_size: u32 = 0;

/// Editor free-look camera override (null = use a scene camera component).
pub var editor_cam: ?types.EditorCam = null;

/// Bloom mip chain cached per-(w,h). Each mip halves resolution from the
/// previous; `mip_count` is however many fit before a dimension drops below ~16px.
pub const MAX_BLOOM_MIPS = 6;
pub const MAX_BLOOM_TARGETS = 3;
pub const BloomChain = struct {
    mips: [MAX_BLOOM_MIPS]?*c.SDL_GPUTexture = .{null} ** MAX_BLOOM_MIPS,
    mip_w: [MAX_BLOOM_MIPS]u32 = .{0} ** MAX_BLOOM_MIPS,
    mip_h: [MAX_BLOOM_MIPS]u32 = .{0} ** MAX_BLOOM_MIPS,
    mip_count: usize = 0,
    w: u32 = 0,
    h: u32 = 0,
};
pub var bloom_targets: [MAX_BLOOM_TARGETS]BloomChain = .{BloomChain{}} ** MAX_BLOOM_TARGETS;
pub var bloom_evict_cursor: usize = 0;

/// The cached bloom chain matching `w`x`h`, or null if none is allocated yet.
pub fn findBloomChain(w: u32, h: u32) ?*BloomChain {
    for (&bloom_targets) |*b| {
        if (b.mip_count != 0 and b.w == w and b.h == h) return b;
    }
    return null;
}

/// A pair of same-size HDR scratch textures, used to ping-pong custom
/// post-process effects (each effect's src/dst must be distinct textures).
/// Cached per-(w,h) with round-robin eviction.
pub const MAX_SCRATCH_TARGETS = 3;
pub const ScratchPair = struct {
    a: ?*c.SDL_GPUTexture = null,
    b: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var scratch_targets: [MAX_SCRATCH_TARGETS]ScratchPair = .{ScratchPair{}} ** MAX_SCRATCH_TARGETS;
pub var scratch_evict_cursor: usize = 0;

/// The cached scratch pair matching `w`x`h`, or null if none is allocated yet.
pub fn findScratchPair(w: u32, h: u32) ?*ScratchPair {
    for (&scratch_targets) |*s| {
        if (s.a != null and s.w == w and s.h == h) return s;
    }
    return null;
}

/// Post-process graphics pipelines, created lazily and shared across every
/// viewport resolution (pipelines bake in format + sample count, not size).
pub var post_threshold_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var post_downsample_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var post_upsample_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var post_composite_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

/// 1x1 black texture substituted for the bloom source when bloom is disabled.
pub var black_tex: ?*c.SDL_GPUTexture = null;

/// Fence-bracketed per-pass GPU timing; off by default (introduces a pipeline stall).
pub var detailed_gpu_timing: bool = false;

/// Runtime rendering feature set — the quality dial `ProjectSettings.graphics.quality`
/// seeds. Every default reproduces the renderer's previously hardcoded behaviour,
/// so an untouched `Features` renders exactly as before.
pub const Features = struct {
    /// Scene-pass MSAA samples; clamped to what the device actually supports.
    /// Pipelines bake this in, so a change rebuilds the scene pipeline cache.
    msaa: u8 = 4,
    ssao: bool = true,
    ssao_half_res: bool = false,
    /// Hemisphere taps, up to `MAX_SSAO_SAMPLES`.
    ssao_samples: u32 = 24,
    ssr: bool = true,
    ssr_half_res: bool = false,
    /// March steps, up to `MAX_SSR_STEPS`.
    ssr_steps: u32 = 32,
    shadows: bool = true,
    /// Edge of one cascade's square atlas strip.
    shadow_dim: u32 = 2048,
    /// Cascades actually fitted and rendered, 1..`types.NUM_CASCADES`. The
    /// arrays stay sized for the comptime maximum either way.
    cascade_count: u32 = types.NUM_CASCADES,
    reflection_probes: bool = true,
    bloom: bool = true,
};

pub var features: Features = .{};

/// A feature set requested between frames, applied at the top of the next
/// `renderScene` — that is where the GPU device is in hand to release whatever
/// the change invalidates, so callers (Studio UI, game startup) need no device.
pub var pending_features: ?Features = null;

// Gizmo line rendering: depth-tested and overlay pipelines, indexed separately to avoid clobber.
pub var gizmo_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var gizmo_overlay_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var gizmo_vtx_buf: [2]?*c.SDL_GPUBuffer = .{ null, null };
pub var gizmo_vtx_cap: [2]usize = .{ 0, 0 };

// Asset sources (GUID → bytes).
pub var mesh_src: ?types.SourceFn = null;
pub var texture_src: ?types.SourceFn = null;
pub var material_src: ?types.SourceFn = null;

// In-memory material override for live editor previews.
pub const OVERRIDE_KEY_CAP = 64;
pub var material_override_key: [OVERRIDE_KEY_CAP]u8 = undefined;
pub var material_override_key_len: usize = 0;
pub var material_override_bytes: []const u8 = &.{};

// GPU resource caches keyed by asset GUID (≤36 chars).
pub const KEY_CAP = 64;

/// Default GPU mesh cache capacity; grows on demand via `ensureMeshCapacity`.
pub const MAX_MESHES = 64;
/// One drawable range of a GPU mesh's index buffer, bound to a material slot.
pub const GpuSubmesh = struct {
    index_offset: u32 = 0,
    index_count: u32 = 0,
    material_slot: i32 = 0,
    /// Per-submesh AABB for frustum culling; falls back to whole-mesh bounds when absent.
    bounds_min: [3]f32 = .{ 0, 0, 0 },
    bounds_max: [3]f32 = .{ 0, 0, 0 },
    /// Cached result of `reflection_probes.resolveForSubmesh`, keyed against
    /// `reflection_probes_generation` — a mismatch means the assignment is
    /// stale and must be re-resolved. -1 = no probe (global env only).
    probe_gen: u32 = 0xFFFF_FFFF,
    probe_slot: i32 = -1,
    probe_weight: f32 = 0,
};
/// How a material occludes light. Blended surfaces transmit it and cast
/// nothing; masked ones occlude only where their cutout test keeps them.
pub const ShadowOcclusion = enum { solid, cutout, none };

/// One material group's resolved shadow-pass state. Cached per frame so the
/// cascades share one resolve instead of repeating it per cascade.
pub const ShadowGroup = struct {
    occlusion: ShadowOcclusion = .none,
    /// Cutout groups only: the cutoff uniform and the albedo map its discard reads.
    fub: types.ShadowMaskFragUB = .{ .flags = .{ 0, 0, 0, 0 }, .base_color = .{ 0, 0, 0, 0 } },
    albedo: ?*c.SDL_GPUTexture = null,
};

/// A same-material run of `submeshes` that forms one indirect multi-draw call.
pub const MaterialGroup = struct {
    material_slot: i32 = 0,
    start: u32 = 0,
    count: u32 = 0,
    /// Indices across every submesh in the run, summed at upload. An upper
    /// bound on what draws, since the GPU cull may zero some instances.
    index_count: u32 = 0,
};

pub const GpuMesh = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    vtx_buf: *c.SDL_GPUBuffer = undefined,
    idx_buf: *c.SDL_GPUBuffer = undefined,
    idx_count: u32 = 0,
    /// Per-submesh draw ranges, sorted by material slot at upload.
    submeshes: []GpuSubmesh = &.{},
    /// Same-material runs of `submeshes` for indirect multi-draw.
    material_groups: []MaterialGroup = &.{},
    /// `material_groups`-parallel shadow state, resolved once per frame by
    /// `shadow.zig` and read by every cascade.
    shadow_groups: []ShadowGroup = &.{},
    /// Frame and mesh renderer `shadow_groups` was resolved against. Two
    /// renderers can share a mesh with different material slots, so a resolve
    /// is only reusable for the same owner within the same frame.
    shadow_groups_frame: u64 = 0,
    shadow_groups_owner: ?*const engine.MeshRendererComponent = null,
    /// Whole-mesh AABB for frustum culling.
    bounds_min: [3]f32 = .{ 0, 0, 0 },
    bounds_max: [3]f32 = .{ 0, 0, 0 },

    /// Per-submesh bounds buffer for the cull compute pass.
    bounds_buf: ?*c.SDL_GPUBuffer = null,
    /// Indirect draw command buffer written by the cull compute pass each frame.
    indirect_buf: ?*c.SDL_GPUBuffer = null,
    /// Indirect draw commands for the shadow pass, one buffer per cascade —
    /// each culled against that cascade's own (tighter) light frustum, not a
    /// single whole-scene one, so cascades don't all redraw the same overdraw.
    shadow_indirect_bufs: [types.NUM_CASCADES]?*c.SDL_GPUBuffer = .{null} ** types.NUM_CASCADES,
    /// Monotonic frame counter: only the first mesh renderer instance referencing
    /// this mesh per frame uses the GPU-driven path; later instances fall back to CPU.
    cull_dispatched_frame: u64 = 0,
    /// As `cull_dispatched_frame`, but per-cascade for the shadow cull dispatch.
    shadow_cull_dispatched_frame: [types.NUM_CASCADES]u64 = .{0} ** types.NUM_CASCADES,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var meshes: []GpuMesh = &.{};
pub var mesh_count: usize = 0;
/// Per-frame monotonic counter for detecting multi-instanced meshes.
pub var frame_seq: u64 = 0;

/// Default GPU texture cache capacity; grows on demand.
pub const MAX_TEXTURES = 64;

/// Environment texture metadata: mip count and order-2 SH diffuse-irradiance coefficients.
pub const EnvironmentData = struct {
    mip_count: u32 = 1,
    sh: [9][3]f32 = @splat(@splat(0)),
    /// Un-prefiltered equirect->cubemap conversion (1 mip) — the sampling
    /// source for `prefiltered_cubemap`; kept around for that reason, never
    /// sampled directly by the scene shader.
    base_cubemap: ?*c.SDL_GPUTexture = null,
    /// GGX-importance-sampled specular cubemap (`ibl_prefilter.zig`), sampled
    /// by `scene.frag.glsl` in place of the old equirect-mip approximation.
    prefiltered_cubemap: ?*c.SDL_GPUTexture = null,
};

pub const GpuTexture = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    texture: *c.SDL_GPUTexture = undefined,
    /// Non-null only for environment maps uploaded via `assets.uploadEnvironment`.
    env: ?EnvironmentData = null,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var textures: []GpuTexture = &.{};
pub var texture_count: usize = 0;

/// Default resolved-material cache capacity; grows on demand.
pub const MAX_RESOLVED_MATERIALS = 128;

/// A material GUID resolved (JSON parsed once) into ready-to-bind values, cached
/// so `resolveMaterial` need not re-read the `.material` file and re-parse it
/// every frame for every draw.
pub const ResolvedMaterialEntry = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    value: types.ResolvedMaterial = .{},

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var resolved_materials: []ResolvedMaterialEntry = &.{};
pub var resolved_material_count: usize = 0;

/// Open-addressed GUID hash → cache-index map, so the mesh/texture/material
/// caches resolve a GUID without scanning every entry. Authoritative: a miss
/// means the GUID is not cached, unless the table has `overflowed`, in which
/// case lookups fall back to a linear scan.
pub const GuidIndex = struct {
    /// Power of two, kept at most half full. Sized past Bistro's 633 textures.
    pub const CAP: usize = 4096;
    const EMPTY: u64 = 0;

    hashes: [CAP]u64 = @splat(EMPTY),
    values: [CAP]u32 = @splat(0),
    count: usize = 0,
    /// Set once the table fills; lookups then defer to a scan rather than lie.
    overflowed: bool = false,

    /// Never returns `EMPTY`, which marks a free slot.
    fn hash(k: []const u8) u64 {
        const h = std.hash.Wyhash.hash(0, k);
        return if (h == EMPTY) 1 else h;
    }

    pub fn clear(self: *GuidIndex) void {
        @memset(&self.hashes, EMPTY);
        self.count = 0;
        self.overflowed = false;
    }

    pub fn put(self: *GuidIndex, k: []const u8, value: u32) void {
        if (self.count * 2 >= CAP) {
            self.overflowed = true;
            return;
        }
        const h = hash(k);
        var i = h & (CAP - 1);
        while (self.hashes[i] != EMPTY) : (i = (i + 1) & (CAP - 1)) {
            if (self.hashes[i] == h) {
                self.values[i] = value;
                return;
            }
        }
        self.hashes[i] = h;
        self.values[i] = value;
        self.count += 1;
    }

    /// The index stored for `k`, or null. Callers must still confirm the entry's
    /// own key matches, since distinct GUIDs can share a hash.
    pub fn get(self: *const GuidIndex, k: []const u8) ?u32 {
        const h = hash(k);
        var i = h & (CAP - 1);
        while (self.hashes[i] != EMPTY) : (i = (i + 1) & (CAP - 1)) {
            if (self.hashes[i] == h) return self.values[i];
        }
        return null;
    }
};

pub var mesh_index: GuidIndex = .{};
pub var texture_index: GuidIndex = .{};
pub var material_index: GuidIndex = .{};

fn growCache(comptime T: type, cur: []T, default_cap: usize) []T {
    var new_cap: usize = if (cur.len == 0) default_cap else cur.len * 2;
    if (new_cap == 0) new_cap = default_cap;
    return if (cur.len == 0)
        std.heap.page_allocator.alloc(T, new_cap) catch cur
    else
        std.heap.page_allocator.realloc(cur, new_cap) catch cur;
}

/// Ensures `meshes` has room for at least one more entry, growing if needed.
pub fn ensureMeshCapacity() void {
    if (mesh_count < meshes.len) return;
    meshes = growCache(GpuMesh, meshes, MAX_MESHES);
}

/// Ensures `textures` has room for at least one more entry.
pub fn ensureTextureCapacity() void {
    if (texture_count < textures.len) return;
    textures = growCache(GpuTexture, textures, MAX_TEXTURES);
}

/// Ensures `resolved_materials` has room for at least one more entry.
pub fn ensureResolvedMaterialCapacity() void {
    if (resolved_material_count < resolved_materials.len) return;
    resolved_materials = growCache(ResolvedMaterialEntry, resolved_materials, MAX_RESOLVED_MATERIALS);
}

const std = @import("std");

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

test "GuidIndex round-trips keys and reports absent ones" {
    var idx: GuidIndex = .{};
    idx.put("aaaa-bbbb", 7);
    idx.put("cccc-dddd", 9);

    try testing.expectEqual(@as(?u32, 7), idx.get("aaaa-bbbb"));
    try testing.expectEqual(@as(?u32, 9), idx.get("cccc-dddd"));
    try testing.expectEqual(@as(?u32, null), idx.get("never-inserted"));
}

test "GuidIndex overwrites rather than duplicating a repeated key" {
    var idx: GuidIndex = .{};
    idx.put("same", 1);
    idx.put("same", 2);
    try testing.expectEqual(@as(?u32, 2), idx.get("same"));
    try testing.expectEqual(@as(usize, 1), idx.count);
}

test "GuidIndex clears back to empty" {
    var idx: GuidIndex = .{};
    idx.put("x", 3);
    idx.clear();
    try testing.expectEqual(@as(?u32, null), idx.get("x"));
    try testing.expectEqual(@as(usize, 0), idx.count);
    try testing.expect(!idx.overflowed);
}

test "GuidIndex flags overflow instead of looping forever when full" {
    var idx: GuidIndex = .{};
    var buf: [32]u8 = undefined;
    for (0..GuidIndex.CAP) |i| {
        const k = try std.fmt.bufPrint(&buf, "guid-{d}", .{i});
        idx.put(k, @intCast(i));
    }
    try testing.expect(idx.overflowed);
    // Half the table is the cap, so it stops inserting well before wrapping.
    try testing.expect(idx.count * 2 <= GuidIndex.CAP);
}
