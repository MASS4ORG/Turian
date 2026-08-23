//! Editor-viewport adapter over the shared `render` module.
//!
//! The actual SDL3-GPU renderer lives in the engine-independent `render` module
//! (used by the built game too). This thin shim wires dvui's GPU device and
//! offscreen TextureTarget into it, and feeds it scene nodes + asset bytes from
//! the editor (`EditorState` + the cooked `.cache`). dvui still owns the window;
//! the renderer just draws into the dvui target.
const std = @import("std");
const gui = @import("gui");
const gpu = @import("gpu");
const engine = @import("engine");
const editor = @import("editor");
const render = @import("render");
const EditorState = @import("editor").EditorState;
const GizmoSystem = @import("GizmoSystem.zig");

const dc = gui.backend.c;
const SDLBackend = gui.backend.SDLBackend;
const page = std.heap.page_allocator;
const log = std.log.scoped(.gpu_renderer);

/// dvui's TextureTarget wraps an SDL_GPUTexture + sampler; we draw into the
/// texture and hand the target back to dvui for display.
const BackendTex = extern struct {
    texture: *dc.SDL_GPUTexture,
    sampler: *dc.SDL_GPUSampler,
};

var g_backend: ?*SDLBackend = null;
var g_cmd: ?*dc.SDL_GPUCommandBuffer = null;
var g_color_target: ?gui.TextureTarget = null;
var g_target_w: u32 = 0;
var g_target_h: u32 = 0;
var g_ready = false;

/// While Play mode runs, render this live node slice (owned by the play library)
/// instead of the editor's edit-time scene. Cleared on Stop.
var g_render_override: ?[]const engine.SceneNode = null;

pub fn setRenderOverride(nodes: ?[]const engine.SceneNode) void {
    g_render_override = nodes;
}

/// When true, the editor gizmo overlay (built by `GizmoSystem`) is drawn over
/// the scene. The viewport enables this in edit mode only.
var g_gizmos_enabled: bool = false;

pub fn setGizmosEnabled(on: bool) void {
    g_gizmos_enabled = on;
}

/// Impose (or clear) the editor free-look camera on the viewport.
pub fn setEditorCamera(cam: ?render.EditorCam) void {
    render.setEditorCamera(cam);
}

/// The camera the renderer would use this frame for `w`×`h`, so the viewport can
/// build picking rays and gizmos that line up exactly with the rendered scene.
pub fn cameraFor(w: u32, h: u32) render.Camera {
    const objects = g_render_override orelse EditorState.objects[0..EditorState.object_count];
    return render.sceneCamera(w, h, objects);
}

/// Initialize the renderer with dvui's SDL backend (must be SPIR-V).
pub fn init(backend: *SDLBackend) !void {
    g_backend = backend;
    if (backend.shaderformat != dc.SDL_GPU_SHADERFORMAT_SPIRV) {
        log.warn("Non-SPIRV backend – 3D viewport disabled.", .{});
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

/// Resize-or-create the offscreen target in `slot`, matching `w`×`h`. Shared
/// by every render-to-texture entry point below so each only states what
/// differs (the camera, the object list), not how to manage its target.
fn ensureTarget(slot: *?gui.TextureTarget, cur_w: *u32, cur_h: *u32, w: u32, h: u32) ?gui.TextureTarget {
    const backend = g_backend orelse return null;
    if (w != cur_w.* or h != cur_h.*) {
        if (slot.*) |t| backend.textureDestroyTarget(t);
        slot.* = backend.textureCreateTarget(.{ .width = w, .height = h }) catch null;
        cur_w.* = w;
        cur_h.* = h;
    }
    return slot.*;
}

/// Render the scene into the dvui offscreen target and return it for display.
pub fn renderViewport(w: u32, h: u32) ?gui.TextureTarget {
    if (!g_ready) return null;
    const cmd = g_cmd orelse return null;
    if (w == 0 or h == 0) return null;

    const ct = ensureTarget(&g_color_target, &g_target_w, &g_target_h, w, h) orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(ct.ptr));

    const objects = g_render_override orelse EditorState.objects[0..EditorState.object_count];
    render.renderScene(@ptrCast(cmd), w, h, objects);

    if (g_gizmos_enabled) {
        if (render.hdrColorFor(w, h)) |hdr| {
            const vp = render.sceneCamera(w, h, objects).view_proj.m;
            render.renderGizmos(@ptrCast(cmd), @ptrCast(hdr), w, h, vp, GizmoSystem.worldVertices(), false);
            render.renderGizmos(@ptrCast(cmd), @ptrCast(hdr), w, h, vp, GizmoSystem.overlayVertices(), true);
        }
    }
    render.runPostProcess(@ptrCast(cmd), @ptrCast(bt.texture), w, h, objects);
    return ct;
}

// ── Game panel rendering ─────────────────────────────────────────────────────
// A second, independent offscreen target so the Play-mode "Game" panel
// (a dedicated panel, split out of the Scene viewport) can render the
// scene's own camera simultaneously with the Scene panel's editor-camera
// view — same save/restore-the-global-camera-override idiom as
// `renderPreview` above, just with its own target so the two don't stomp
// each other's contents within the same frame.

var g_game_target: ?gui.TextureTarget = null;
var g_game_w: u32 = 0;
var g_game_h: u32 = 0;

/// Render `objects` under the scene's own camera (no editor-camera override)
/// into a reusable `w`×`h` offscreen target. Live/per-frame use, like
/// `renderPreview` — don't hold the returned handle across frames.
pub fn renderGameViewport(objects: []const engine.SceneNode, w: u32, h: u32) ?gui.TextureTarget {
    if (!g_ready) return null;
    const cmd = g_cmd orelse return null;
    if (w == 0 or h == 0) return null;

    const target = ensureTarget(&g_game_target, &g_game_w, &g_game_h, w, h) orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(target.ptr));

    const saved_cam = render.editorCamera();
    render.setEditorCamera(null);
    render.renderScene(@ptrCast(cmd), w, h, objects);
    render.runPostProcess(@ptrCast(cmd), @ptrCast(bt.texture), w, h, objects);
    render.setEditorCamera(saved_cam);
    return target;
}

/// RGBA8 CPU pixels downloaded from the viewport color target, plus its size.
pub const Capture = struct {
    /// RGBA8 pixels, `w*h*4` bytes, owned by the caller's allocator.
    pixels: []u8,
    w: u32,
    h: u32,
};

// ── Asset preview rendering ──────────────────────────────────────────────────
// Renders an arbitrary (usually synthetic, 1-2 node) scene into its own
// offscreen target under a caller-supplied camera, independent of the main
// editor viewport. Used by `PreviewSystem` (thumbnail generation) and
// `MaterialEditor` (live interactive preview).

var g_preview_target: ?gui.TextureTarget = null;
var g_preview_w: u32 = 0;
var g_preview_h: u32 = 0;

/// Render `objects` under `cam` into a reusable `w`×`h` offscreen target and
/// return it for display via `gui.Texture.fromTargetTemp`. Live/per-frame use
/// (e.g. an interactive orbiting preview) — the target's contents change every
/// call, so don't hold onto the returned handle across frames.
pub fn renderPreview(objects: []const engine.SceneNode, cam: render.EditorCam, w: u32, h: u32) ?gui.TextureTarget {
    if (!g_ready) return null;
    const cmd = g_cmd orelse return null;
    if (w == 0 or h == 0) return null;

    const target = ensureTarget(&g_preview_target, &g_preview_w, &g_preview_h, w, h) orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(target.ptr));

    const saved_cam = render.editorCamera();
    render.setEditorCamera(cam);
    render.renderScene(@ptrCast(cmd), w, h, objects);
    render.runPostProcess(@ptrCast(cmd), @ptrCast(bt.texture), w, h, objects);
    render.setEditorCamera(saved_cam);
    return target;
}

// ── Camera-node preview (Inspector "Camera Preview" overlay) ────────────────
// A dedicated target, separate from `g_preview_target` above: that one is
// already shared by the asset-thumbnail/MaterialEditor preview at whatever
// size *they* need, and giving the camera-preview overlay its own slot avoids
// two unrelated consumers thrashing each other's target size every frame.

var g_cam_preview_target: ?gui.TextureTarget = null;
var g_cam_preview_w: u32 = 0;
var g_cam_preview_h: u32 = 0;

/// Render `objects` under `cam` into the camera-preview target. Same
/// save/restore-camera-override idiom as `renderPreview`, kept separate so
/// callers can throttle how often this actually re-renders (see
/// `CameraPreview.zig`) while still calling `cameraPreviewTarget` every frame
/// to redisplay the last result.
pub fn renderCameraPreview(objects: []const engine.SceneNode, cam: render.EditorCam, w: u32, h: u32) ?gui.TextureTarget {
    if (!g_ready) return null;
    const cmd = g_cmd orelse return null;
    if (w == 0 or h == 0) return null;

    const target = ensureTarget(&g_cam_preview_target, &g_cam_preview_w, &g_cam_preview_h, w, h) orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(target.ptr));

    const saved_cam = render.editorCamera();
    render.setEditorCamera(cam);
    render.renderScene(@ptrCast(cmd), w, h, objects);
    render.runPostProcess(@ptrCast(cmd), @ptrCast(bt.texture), w, h, objects);
    render.setEditorCamera(saved_cam);
    return target;
}

/// The camera-preview target as last rendered, without rendering again —
/// lets a throttled caller redisplay the same image between refreshes.
pub fn cameraPreviewTarget() ?gui.TextureTarget {
    return g_cam_preview_target;
}

/// Objects the main viewport is currently drawing: the live Play-mode node
/// slice while a simulation runs, otherwise the editor's scene objects.
pub fn viewportObjects() []const engine.SceneNode {
    return g_render_override orelse EditorState.objects[0..EditorState.object_count];
}

/// Render `objects` under `cam` into a fresh `w`×`h` offscreen target and read
/// the result back to CPU RGBA8 pixels, synchronously. Unlike `renderPreview`,
/// this is self-contained (its own command buffer, submitted and waited on
/// immediately) so the very first call already sees the freshly-rendered
/// content — suitable for one-shot thumbnail generation, not per-frame display.
/// Blocks on the GPU; call only on a cache miss.
pub fn renderAndCapture(allocator: std.mem.Allocator, objects: []const engine.SceneNode, cam: render.EditorCam, w: u32, h: u32) ?Capture {
    if (!g_ready) return null;
    const backend = g_backend orelse return null;
    if (w == 0 or h == 0) return null;

    const target = backend.textureCreateTarget(.{ .width = w, .height = h }) catch return null;
    defer backend.textureDestroyTarget(target);
    const bt: *BackendTex = @ptrCast(@alignCast(target.ptr));

    const dev: *gpu.c.SDL_GPUDevice = @ptrCast(backend.device);
    const own_cmd = gpu.c.SDL_AcquireGPUCommandBuffer(dev) orelse return null;

    const saved_cam = render.editorCamera();
    render.setEditorCamera(cam);
    render.renderScene(own_cmd, w, h, objects);
    render.runPostProcess(own_cmd, @ptrCast(bt.texture), w, h, objects);
    render.setEditorCamera(saved_cam);

    const fence = gpu.c.SDL_SubmitGPUCommandBufferAndAcquireFence(own_cmd) orelse return null;
    _ = gpu.c.SDL_WaitForGPUFences(dev, true, &fence, 1);
    gpu.c.SDL_ReleaseGPUFence(dev, fence);

    const pixels = gpu.captureTexture(dev, allocator, @ptrCast(bt.texture), w, h, gpu.c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM) catch |err| {
        log.err("preview capture failed: {any}", .{err});
        return null;
    };
    return .{ .pixels = pixels, .w = w, .h = h };
}

/// Download the editor viewport color target (the real GPU-rendered scene) to
/// RGBA8 CPU pixels. Caller owns `Capture.pixels`. Null if the
/// renderer/target isn't ready. The pixels come from the most recently
/// *submitted* frame — fine for a debug snapshot.
pub fn capturePixels(allocator: std.mem.Allocator) ?Capture {
    const backend = g_backend orelse return null;
    const ct = g_color_target orelse return null;
    if (g_target_w == 0 or g_target_h == 0) return null;
    const bt: *BackendTex = @ptrCast(@alignCast(ct.ptr));
    const pixels = gpu.captureTexture(
        @ptrCast(backend.device),
        allocator,
        @ptrCast(bt.texture),
        g_target_w,
        g_target_h,
        gpu.c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
    ) catch |err| {
        log.err("capture failed: {any}", .{err});
        return null;
    };
    return .{ .pixels = pixels, .w = g_target_w, .h = g_target_h };
}

// ── VSync (runtime swapchain present mode) ───────────────────────────────────
// The window is created vsync-on; the profiler panel can flip it at runtime so
// fps caps above the refresh rate take effect. dvui acquires the swapchain
// texture in `Window.begin`, so reconfiguring the swapchain *during* a frame
// invalidates that texture and crashes the next render pass. We therefore only
// record the request here and apply it from the main loop between frames, via
// `applyPendingVsync` (called before `Window.begin`).

var g_vsync: bool = true;
var g_vsync_pending: ?bool = null;

/// The effective vsync state, reflecting a pending request so the UI updates
/// immediately.
pub fn vsyncOn() bool {
    return g_vsync_pending orelse g_vsync;
}

/// Request a vsync change; applied between frames by `applyPendingVsync`.
pub fn requestVsync(on: bool) void {
    g_vsync_pending = on;
}

/// Apply a pending vsync change at a safe point (no swapchain texture acquired,
/// no live command buffer). Call once per loop iteration before `Window.begin`.
pub fn applyPendingVsync() void {
    const want = g_vsync_pending orelse return;
    g_vsync_pending = null;
    if (want == g_vsync) return;
    const backend = g_backend orelse return;

    // Off: prefer IMMEDIATE (true uncapped, may tear); fall back to MAILBOX
    // (uncapped render, no tearing) if the platform lacks IMMEDIATE.
    const mode: dc.SDL_GPUPresentMode = if (want) dc.SDL_GPU_PRESENTMODE_VSYNC else blk: {
        if (dc.SDL_WindowSupportsGPUPresentMode(backend.device, backend.window, dc.SDL_GPU_PRESENTMODE_IMMEDIATE))
            break :blk dc.SDL_GPU_PRESENTMODE_IMMEDIATE;
        if (dc.SDL_WindowSupportsGPUPresentMode(backend.device, backend.window, dc.SDL_GPU_PRESENTMODE_MAILBOX))
            break :blk dc.SDL_GPU_PRESENTMODE_MAILBOX;
        log.warn("no uncapped present mode supported; keeping vsync on", .{});
        return;
    };
    if (!dc.SDL_SetGPUSwapchainParameters(backend.device, backend.window, dc.SDL_GPU_SWAPCHAINCOMPOSITION_SDR, mode)) {
        log.err("set vsync={} failed: {s}", .{ want, dc.SDL_GetError() });
        return;
    }
    g_vsync = want;
}

pub fn deinit() void {
    render.deinit();
    if (g_color_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_color_target = null;
    }
    if (g_game_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_game_target = null;
    }
    if (g_preview_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_preview_target = null;
    }
    if (g_cam_preview_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_cam_preview_target = null;
    }
    g_ready = false;
}

// ── Asset sources (GUID → bytes for the render module) ──────────────────────────

fn readOwned(path: []const u8) ?render.Bytes {
    const data = std.Io.Dir.cwd().readFileAlloc(gui.io, path, page, .unlimited) catch return null;
    return .{ .data = data, .owned = true };
}

/// Meshes come from the cooked canonical artifact in `.cache` (one fast loader).
/// Built-in preview primitives (cube/sphere) are generated on the fly instead of
/// resolved through the asset database — the preview system references them by
/// a reserved GUID that doesn't correspond to any project asset.
fn meshBytes(guid: []const u8) ?render.Bytes {
    if (engine.assets.PrimitiveMesh.builtinBytes(page, guid) catch null) |bytes|
        return .{ .data = bytes, .owned = true };
    const proj = EditorState.project_path orelse return null;
    const g = editor.Guid.parse(guid) catch return null;
    var buf: [1024]u8 = undefined;
    const path = editor.asset_cache.artifactPath(proj, g, .model, &buf) orelse return null;
    return readOwned(path);
}

/// Textures resolve to their *uncooked* source file for live preview, so a DDS is cooked in memory here the same way `AssetImporter.cookImage` does at import time — otherwise an sRGB albedo/emissive DDS uploads as plain linear format, and the GPU sampler skips gamma decode.
fn textureBytes(guid: []const u8) ?render.Bytes {
    const path = EditorState.resolveAssetGuid(guid) orelse return null;
    const raw = readOwned(path) orelse return null;
    if (!std.ascii.eqlIgnoreCase(std.fs.path.extension(path), ".dds")) return raw;

    var arena = std.heap.ArenaAllocator.init(page);
    defer arena.deinit();
    const meta = editor.asset_meta.readMeta(gui.io, arena.allocator(), path);
    if (meta.import_settings != .image) return raw;
    const s = meta.import_settings.image;

    const cooked = engine.assets.DdsLoader.cook(page, raw.data, .{
        .srgb = s.color_space == .srgb,
        .flip_green_channel = s.texture_type == .normal_map and s.flip_green_channel,
    }) catch return raw;
    if (raw.owned) page.free(raw.data);
    return .{ .data = cooked, .owned = true };
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
