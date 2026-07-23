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

/// Fixed-function state baked into a scene pipeline permutation. SDL3 GPU (like
/// Vulkan) has no dynamic blend-equation or cull-mode state, so each distinct
/// combination a material asks for needs its own pipeline object.
pub const ScenePipelineState = struct {
    blend: engine.Material.BlendMode = .disabled,
    cull: engine.Material.CullMode = .back,
    depth_write: bool = true,
    depth_test: bool = true,
};

/// Scene pipelines are created lazily and cached by state combo — capped and
/// linearly searched, mirroring `depth_targets` below (the handful of distinct
/// combos a scene's materials use stays well under the cap).
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

/// GPU-driven frustum-culling compute pipeline (see `pipeline.createCullComputePipeline`).
pub var cull_pipeline: ?*c.SDL_GPUComputePipeline = null;

pub var white_tex: ?*c.SDL_GPUTexture = null;
/// Default tangent-space "flat" normal (points straight out): rgb (128,128,255).
pub var flat_normal_tex: ?*c.SDL_GPUTexture = null;

/// Depth targets are cached by (w,h) instead of a single shared texture: the
/// editor records several differently-sized passes into the SAME command buffer
/// each frame (the main viewport, plus 128×128 asset previews). Releasing and
/// recreating one shared depth texture mid-frame — as the old single-slot code
/// did on every size change — frees a depth attachment a not-yet-submitted pass
/// still references, corrupting whichever pass was recorded first (previews came
/// out see-through / fan-shaped). One persistent texture per distinct size means
/// no depth attachment is ever released while a pass in the current frame still
/// points at it.
pub const MAX_DEPTH_TARGETS = 6;
pub const DepthTarget = struct {
    tex: ?*c.SDL_GPUTexture = null,
    w: u32 = 0,
    h: u32 = 0,
};
pub var depth_targets: [MAX_DEPTH_TARGETS]DepthTarget = .{DepthTarget{}} ** MAX_DEPTH_TARGETS;
/// Round-robin eviction cursor, used only once every slot is occupied.
pub var depth_evict_cursor: usize = 0;

/// The cached depth texture matching `w`×`h`, or null if none is allocated yet.
/// Used by the gizmo overlay pass to depth-test against the depth `renderScene`
/// just produced for the same-sized viewport.
pub fn findDepth(w: u32, h: u32) ?*c.SDL_GPUTexture {
    for (&depth_targets) |*d| {
        if (d.tex != null and d.w == w and d.h == h) return d.tex;
    }
    return null;
}

/// Editor free-look camera override (null = use a scene camera component).
pub var editor_cam: ?types.EditorCam = null;

// Gizmo line rendering. Two pipelines: one
// depth-tested (world gizmos occluded by geometry) and one overlay (always on
// top, for manipulation handles). The vertex buffer is grown on demand; index
// 0 holds depth-tested verts, index 1 the overlay verts, so two draws per frame
// never clobber each other's data.
pub var gizmo_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var gizmo_overlay_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var gizmo_vtx_buf: [2]?*c.SDL_GPUBuffer = .{ null, null };
pub var gizmo_vtx_cap: [2]usize = .{ 0, 0 };

// Asset sources (GUID → bytes).
pub var mesh_src: ?types.SourceFn = null;
pub var texture_src: ?types.SourceFn = null;
pub var material_src: ?types.SourceFn = null;

// Material override: when the GUID a mesh_renderer references matches
// `material_override_key`, its bytes are served from `material_override_bytes`
// instead of `material_src`. Lets a live editor panel (e.g. the material
// inspector) preview in-memory edits before they're saved to disk, without
// spamming the filesystem on every slider tweak.
pub const OVERRIDE_KEY_CAP = 64;
pub var material_override_key: [OVERRIDE_KEY_CAP]u8 = undefined;
pub var material_override_key_len: usize = 0;
pub var material_override_bytes: []const u8 = &.{};

// GPU resource caches keyed by asset GUID (≤36 chars).
pub const KEY_CAP = 64;

/// Default/initial GPU mesh cache capacity — not a hard ceiling; `meshes`
/// grows on demand (see `ensureMeshCapacity`). A Bistro-scale FBX hierarchy's
/// unique-mesh count (after instance dedup, see #142) can exceed this.
pub const MAX_MESHES = 64;
/// One drawable range of a GPU mesh's index buffer, bound to a material slot.
/// `material_slot` keys into the mesh renderer's `materials` table (or -1 for no
/// material). A GPU mesh holds one per cooked submesh, with no fixed ceiling.
pub const GpuSubmesh = struct {
    index_offset: u32 = 0,
    index_count: u32 = 0,
    material_slot: i32 = 0,
    /// Local-space AABB for just this submesh's index range, for per-submesh
    /// frustum culling. Falls back to the whole mesh's bounds when the source
    /// mesh had no explicit submesh table (see `Mesh.computeSubmeshBounds`).
    bounds_min: [3]f32 = .{ 0, 0, 0 },
    bounds_max: [3]f32 = .{ 0, 0, 0 },
};
/// A contiguous run of `GpuMesh.submeshes` (after material-sort at upload)
/// sharing one material slot — the unit of one indirect multi-draw call.
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
    /// Heap-owned draw ranges (page allocator), sorted by `material_slot` at
    /// upload so `material_groups` below can address contiguous runs. A large
    /// flattened model can carry thousands, so this is a slice rather than a
    /// fixed array. Empty when the mesh failed to upload.
    submeshes: []GpuSubmesh = &.{},
    /// Contiguous same-material runs of `submeshes`, one indirect multi-draw
    /// call per entry.
    material_groups: []MaterialGroup = &.{},
    /// Local-space AABB corners (whole mesh, all submeshes combined), carried
    /// over from the cooked mesh for frustum culling.
    bounds_min: [3]f32 = .{ 0, 0, 0 },
    bounds_max: [3]f32 = .{ 0, 0, 0 },

    /// Compute-readable per-submesh bounds (one `types.SubmeshBoundsGpu` per
    /// entry in `submeshes`, same order), read-only input to the cull compute
    /// pass. Null when the mesh failed to upload.
    bounds_buf: ?*c.SDL_GPUBuffer = null,
    /// GPU-driven indirect draw command buffer, one `SDL_GPUIndexedIndirectDrawCommand`
    /// per entry in `submeshes` (same order). Written fresh each frame by the
    /// cull compute pass (`num_instances` 0 or 1), then read by
    /// `SDL_DrawGPUIndexedPrimitivesIndirect` — one call per `material_groups`
    /// entry. Null when the mesh failed to upload.
    indirect_buf: ?*c.SDL_GPUBuffer = null,
    /// `frame_seq` value as of this mesh's last cull compute dispatch this
    /// frame. `bounds_buf`/`indirect_buf` are per-mesh (not per-instance), so
    /// only the *first* mesh renderer instance referencing this mesh in a
    /// given frame can safely use the GPU-driven path — a second instance
    /// sharing the same mesh this frame would overwrite the first's indirect
    /// commands before they're drawn. Later instances fall back to the CPU
    /// per-submesh path (see `renderScene`) instead of racing the buffer.
    cull_dispatched_frame: u64 = 0,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var meshes: []GpuMesh = &.{};
pub var mesh_count: usize = 0;
/// Monotonic per-`renderScene`-call counter, compared against
/// `GpuMesh.cull_dispatched_frame` to detect a mesh instanced more than once
/// in the same frame.
pub var frame_seq: u64 = 0;

/// Default/initial GPU texture cache capacity — not a hard ceiling; see
/// `MAX_MESHES`/`ensureMeshCapacity`'s doc comment (same reasoning).
pub const MAX_TEXTURES = 64;

/// Extra data uploaded alongside an equirectangular HDR environment texture
/// (see `assets.uploadEnvironment`): its mip count (for roughness-based
/// `textureLod` specular sampling) and precomputed order-2 spherical harmonics
/// diffuse-irradiance coefficients (9 RGB triplets).
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

/// Ensures `meshes` has room for at least one more entry (at `mesh_count`),
/// growing (doubling from `MAX_MESHES`) if needed. No-op if already large
/// enough. Callers must still check `mesh_count < meshes.len` before writing
/// — growth can fail under memory pressure.
pub fn ensureMeshCapacity() void {
    if (mesh_count < meshes.len) return;
    meshes = growCache(GpuMesh, meshes, MAX_MESHES);
}

/// Ensures `textures` has room for at least one more entry, mirroring
/// `ensureMeshCapacity`.
pub fn ensureTextureCapacity() void {
    if (texture_count < textures.len) return;
    textures = growCache(GpuTexture, textures, MAX_TEXTURES);
}

const std = @import("std");
