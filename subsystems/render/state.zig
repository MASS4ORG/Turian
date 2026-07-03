//! Shared GPU state for the renderer (global singleton — one renderer per
//! process). Other render files read/write this; the public API lives in
//! `root.zig`.
const gpu = @import("gpu");
const types = @import("types.zig");

const c = gpu.c;

pub const SHADOW_FORMAT = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;

pub var device: ?*c.SDL_GPUDevice = null;
pub var pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var sampler: ?*c.SDL_GPUSampler = null;

pub var shadow_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var shadow_map: ?*c.SDL_GPUTexture = null;
pub var shadow_sampler: ?*c.SDL_GPUSampler = null;

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

pub const MAX_MESHES = 64;
pub const GpuMesh = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    vtx_buf: *c.SDL_GPUBuffer = undefined,
    idx_buf: *c.SDL_GPUBuffer = undefined,
    idx_count: u32 = 0,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var meshes: [MAX_MESHES]GpuMesh = undefined;
pub var mesh_count: usize = 0;

pub const MAX_TEXTURES = 64;
pub const GpuTexture = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    texture: *c.SDL_GPUTexture = undefined,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var textures: [MAX_TEXTURES]GpuTexture = undefined;
pub var texture_count: usize = 0;

const std = @import("std");
