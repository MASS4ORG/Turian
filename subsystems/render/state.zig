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
pub var shadow_map: ?*c.SDL_GPUTexture = null;
pub var shadow_sampler: ?*c.SDL_GPUSampler = null;

pub var skybox_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

/// Depth+normal prepass and SSAO pipelines. Null if creation failed —
/// `renderScene` falls back to `white_tex` for the AO input in that case.
pub var prepass_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var ssao_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var ssao_blur_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;

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
};
/// A same-material run of `submeshes` that forms one indirect multi-draw call.
pub const MaterialGroup = struct {
    material_slot: i32 = 0,
    start: u32 = 0,
    count: u32 = 0,
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
