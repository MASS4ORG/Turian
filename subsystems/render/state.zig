//! Shared GPU state for the renderer (global singleton — one renderer per
//! process). Other render files read/write this; the public API lives in
//! `root.zig`.
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");

const c = gpu.c;

pub const SHADOW_FORMAT = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;

pub var device: ?*c.SDL_GPUDevice = null;
pub var sampler: ?*c.SDL_GPUSampler = null;

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

/// GPU-driven frustum-culling compute pipeline.
pub var cull_pipeline: ?*c.SDL_GPUComputePipeline = null;

pub var white_tex: ?*c.SDL_GPUTexture = null;
/// Default tangent-space "flat" normal (points straight out): rgb (128,128,255).
pub var flat_normal_tex: ?*c.SDL_GPUTexture = null;

/// Depth targets cached by (w,h) so no attachment is released while a pass in the current frame still references it.
pub const MAX_DEPTH_TARGETS = 6;
pub const DepthTarget = struct {
    tex: ?*c.SDL_GPUTexture = null,
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

/// Editor free-look camera override (null = use a scene camera component).
pub var editor_cam: ?types.EditorCam = null;

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
    /// Monotonic frame counter: only the first mesh renderer instance referencing
    /// this mesh per frame uses the GPU-driven path; later instances fall back to CPU.
    cull_dispatched_frame: u64 = 0,

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

const std = @import("std");
