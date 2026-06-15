//! Editor-viewport adapter over the shared `render` module.
//!
//! The actual SDL3-GPU renderer lives in the engine-independent `render` module
//! (used by the built game too). This thin shim wires dvui's GPU device and
//! offscreen TextureTarget into it, and feeds it scene nodes + asset bytes from
//! the editor (`EditorState` + the cooked `.cache`). dvui still owns the window;
//! the renderer just draws into the dvui target.
const std = @import("std");
const dvui = @import("dvui");
const gpu = @import("gpu");
const engine = @import("engine");
const editor = @import("editor");
const render = @import("render");
const EditorState = @import("EditorState.zig");

const dc = dvui.backend.c;
const SDLBackend = dvui.backend.SDLBackend;
const page = std.heap.page_allocator;

/// dvui's TextureTarget wraps an SDL_GPUTexture + sampler; we draw into the
/// texture and hand the target back to dvui for display.
const BackendTex = extern struct {
    texture: *dc.SDL_GPUTexture,
    sampler: *dc.SDL_GPUSampler,
};

var g_backend: ?*SDLBackend = null;
var g_cmd: ?*dc.SDL_GPUCommandBuffer = null;
var g_color_target: ?dvui.TextureTarget = null;
var g_target_w: u32 = 0;
var g_target_h: u32 = 0;
var g_ready = false;

/// While Play mode runs, render this live node slice (owned by the play library)
/// instead of the editor's edit-time scene. Cleared on Stop (issue #31).
var g_render_override: ?[]const engine.SceneNode = null;

pub fn setRenderOverride(nodes: ?[]const engine.SceneNode) void {
    g_render_override = nodes;
}

/// Initialize the renderer with dvui's SDL backend (must be SPIR-V).
pub fn init(backend: *SDLBackend) !void {
    g_backend = backend;
    if (backend.shaderformat != dc.SDL_GPU_SHADERFORMAT_SPIRV) {
        std.debug.print("[GpuRenderer] Non-SPIRV backend – 3D viewport disabled.\n", .{});
        return;
    }
    // dvui's SDL device and the render module's SDL device are the same library,
    // surfaced through two translate-c modules — reinterpret the pointer.
    try render.init(@ptrCast(backend.device));
    render.setSources(meshBytes, textureBytes, materialBytes);
    g_ready = true;
}

/// Record the command buffer dvui acquired for this frame.
pub fn beginFrame(cmd: ?*dc.SDL_GPUCommandBuffer) void {
    g_cmd = cmd;
}

/// Render the scene into the dvui offscreen target and return it for display.
pub fn renderViewport(w: u32, h: u32) ?dvui.TextureTarget {
    if (!g_ready) return null;
    const cmd = g_cmd orelse return null;
    const backend = g_backend orelse return null;
    if (w == 0 or h == 0) return null;

    if (w != g_target_w or h != g_target_h) {
        if (g_color_target) |ct| backend.textureDestroyTarget(ct);
        g_color_target = backend.textureCreateTarget(.{ .width = w, .height = h }) catch null;
        g_target_w = w;
        g_target_h = h;
    }
    const ct = g_color_target orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(ct.ptr));

    const objects = g_render_override orelse EditorState.objects[0..EditorState.object_count];
    render.renderScene(@ptrCast(cmd), @ptrCast(bt.texture), w, h, objects);
    return ct;
}

pub fn deinit() void {
    render.deinit();
    if (g_color_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_color_target = null;
    }
    g_ready = false;
}

// ── Asset sources (GUID → bytes for the render module) ──────────────────────────

fn readOwned(path: []const u8) ?render.Bytes {
    const data = std.Io.Dir.cwd().readFileAlloc(dvui.io, path, page, .unlimited) catch return null;
    return .{ .data = data, .owned = true };
}

/// Meshes come from the cooked canonical artifact in `.cache` (one fast loader).
fn meshBytes(guid: []const u8) ?render.Bytes {
    const proj = EditorState.project_path orelse return null;
    const g = editor.Guid.parse(guid) catch return null;
    var buf: [1024]u8 = undefined;
    const path = editor.asset_cache.artifactPath(proj, g, .model, &buf) orelse return null;
    return readOwned(path);
}

/// Textures come from the asset path the database resolved (source image, or a
/// cooked `.texture` for embedded/derived images).
fn textureBytes(guid: []const u8) ?render.Bytes {
    const path = EditorState.resolveAssetGuid(guid) orelse return null;
    return readOwned(path);
}

/// Materials: built-in presets are serialized on the fly; others read from disk.
fn materialBytes(guid: []const u8) ?render.Bytes {
    var buf: [64 * 1024]u8 = undefined;
    if (engine.Material.builtinBytes(guid, &buf)) |bytes| {
        const data = page.dupe(u8, bytes) catch return null;
        return .{ .data = data, .owned = true };
    }
    const path = EditorState.resolveAssetGuid(guid) orelse return null;
    return readOwned(path);
}
