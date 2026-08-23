//! Unity-style camera preview: when the selection is a single node with a
//! Camera component, renders that camera's view into a small bottom-right
//! inset over the Scene viewport, so positioning a camera doesn't require
//! guessing from raw transform numbers.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const render = @import("render");
const EditorState = @import("editor").EditorState;
const GpuRenderer = @import("GpuRenderer.zig");

const PREVIEW_W: f32 = 320;
const PREVIEW_H: f32 = 180;
const MARGIN: f32 = 12;
/// A full scene render (shadows, SSR, reflection probes, post-process) is
/// resolution-independent in CPU cost, so re-rendering this 320x180 inset
/// every frame would roughly double edit-mode's per-frame cost. Throttling
/// keeps it a framing aid instead of a second full viewport.
const REFRESH_NS: i128 = 100 * std.time.ns_per_ms;

var g_last_render_ns: i128 = 0;
var g_last_idx: ?usize = null;
var g_last_transform: engine.Transform = .{};
var g_last_fov: f32 = 0;

/// The selection, iff it is exactly one node carrying a Camera component.
pub fn selectedCameraIndex() ?usize {
    if (EditorState.selectedCount() != 1) return null;
    const sel = EditorState.selected_object orelse return null;
    if (sel >= EditorState.object_count) return null;
    const obj = &EditorState.objects[sel];
    for (obj.components[0..obj.component_count]) |*c| {
        if (c.* == .camera) return sel;
    }
    return null;
}

/// Builds the render camera from `obj`'s own transform + Camera component,
/// using the full rotation (including roll) so the preview matches what
/// `sceneCamera` would actually show in the Game view.
fn poseFor(obj: *const engine.SceneNode) ?render.EditorCam {
    for (obj.components[0..obj.component_count]) |*comp| {
        if (comp.* != .camera) continue;
        return .{
            .pos = obj.transform.position,
            .rot = obj.transform.rotation,
            .fov = comp.camera.fov,
            .near = comp.camera.near,
            .far = comp.camera.far,
        };
    }
    return null;
}

/// Draw the inset, if the current selection is a single camera node. Call
/// from inside the Scene viewport's overlay.
pub fn draw() void {
    const idx = selectedCameraIndex() orelse return;
    const obj = &EditorState.objects[idx];
    const cam = poseFor(obj) orelse return;

    var box = gui.box(@src(), .{}, .{
        .min_size_content = .{ .w = PREVIEW_W, .h = PREVIEW_H },
        .gravity_x = 1.0,
        .gravity_y = 1.0,
        .margin = .all(MARGIN),
        .background = true,
        .style = .content,
        .border = .all(1),
    });
    defer box.deinit();

    const scale = gui.windowNaturalScale();
    const w: u32 = @max(1, @as(u32, @intFromFloat(PREVIEW_W * scale)));
    const h: u32 = @max(1, @as(u32, @intFromFloat(PREVIEW_H * scale)));

    const now = gui.frameTimeNS();
    const idx_changed = if (g_last_idx) |li| li != idx else true;
    const stale = idx_changed or !std.meta.eql(g_last_transform, obj.transform) or g_last_fov != cam.fov;
    const target = if (stale or now - g_last_render_ns >= REFRESH_NS) blk: {
        g_last_idx = idx;
        g_last_transform = obj.transform;
        g_last_fov = cam.fov;
        g_last_render_ns = now;
        break :blk GpuRenderer.renderCameraPreview(GpuRenderer.viewportObjects(), cam, w, h);
    } else GpuRenderer.cameraPreviewTarget();

    if (target) |t| {
        if (gui.Texture.fromTargetTemp(t) catch null) |tex| {
            _ = gui.image(@src(), .{ .source = .{ .texture = tex } }, .{ .expand = .both });
        }
    }
}
