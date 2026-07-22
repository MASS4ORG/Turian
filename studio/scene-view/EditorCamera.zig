//! The Scene View's free-look navigation camera.
//!
//! The editor viewport renders from this camera, not from a scene `Camera`
//! component, so you can fly around to inspect the scene without disturbing the
//! game's cameras. Hold the right mouse button to look; while held, WASD moves,
//! Q/E (or Space) drop/raise, and Shift accelerates. Hold the middle mouse
//! button to pan (matches Unity/Godot). The mouse wheel dollies in and out at
//! any time. The pose is pushed to the `render` module each edit frame via
//! `render.setEditorCamera`.
const std = @import("std");
const engine = @import("engine");
const render = @import("render");

const Vector3 = engine.Vector3;
const Matrix4 = engine.Matrix4;

var pos: Vector3 = .{ .x = 0, .y = 2, .z = -6 };
var yaw: f32 = 0; // degrees, around world Y
var pitch: f32 = 0; // degrees, around local X
var fov: f32 = 60;
var initialized = false;

/// Navigation tuning, persisted via the editor Settings API (see
/// `SceneViewport`). These are live-editable so the user can fine-tune the feel
/// of the free-look camera; the defaults match the original hard-coded values.
pub var move_speed: f32 = 4.0; // world units / second (WASDQE)
pub var look_sensitivity: f32 = 0.18; // degrees / pixel (RMB look)
pub var zoom_speed: f32 = 0.05; // world units / wheel notch (dolly)
pub var pan_speed: f32 = 0.015; // world units / pixel (MMB pan)

/// Shift multiplier applied to `move_speed` for fast travel.
const FAST_MULTIPLIER: f32 = 3.5;

/// Per-frame navigation input gathered by the viewport.
pub const Nav = struct {
    rmb_down: bool = false,
    look_dx: f32 = 0,
    look_dy: f32 = 0,
    /// Middle-mouse-drag pan deltas, in screen pixels (see `EditorCamera`'s doc
    /// comment). Applied regardless of `rmb_down`.
    pan_dx: f32 = 0,
    pan_dy: f32 = 0,
    wheel: f32 = 0,
    forward: bool = false,
    back: bool = false,
    left: bool = false,
    right: bool = false,
    up: bool = false,
    down: bool = false,
    fast: bool = false,
    dt: f32 = 0,
};

/// Snapshot of the free-look camera pose, saved/restored per document tab so
/// each open scene keeps its own viewpoint.
pub const State = struct {
    pos: Vector3 = .{ .x = 0, .y = 2, .z = -6 },
    yaw: f32 = 0,
    pitch: f32 = 0,
    fov: f32 = 60,
    initialized: bool = false,
};

pub fn getState() State {
    return .{ .pos = pos, .yaw = yaw, .pitch = pitch, .fov = fov, .initialized = initialized };
}

pub fn setState(s: State) void {
    pos = s.pos;
    yaw = s.yaw;
    pitch = s.pitch;
    fov = s.fov;
    initialized = s.initialized;
}

/// Forget the current pose so the next `ensureInit` re-seeds from the (new)
/// scene's camera. Used when a fresh scene tab is opened.
pub fn reset() void {
    initialized = false;
}

/// On first use, seed the editor camera from the scene's first camera component
/// so the viewport opens looking at roughly what the game would show.
pub fn ensureInit(objects: []const engine.SceneNode, count: usize) void {
    if (initialized) return;
    initialized = true;
    for (objects[0..count]) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .camera) {
                pos = obj.transform.position;
                pitch = obj.transform.rotation.x;
                yaw = obj.transform.rotation.y;
                fov = comp.camera.fov;
                return;
            }
        }
    }
}

/// The current pose, ready to hand to `render.setEditorCamera`.
pub fn pose() render.EditorCam {
    return .{ .pos = pos, .rot = .{ .x = pitch, .y = yaw, .z = 0 }, .fov = fov };
}

fn basis() struct { fwd: Vector3, right: Vector3, up: Vector3 } {
    const rm = Matrix4.rotationEuler(pitch, yaw, 0);
    return .{
        .fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 }),
        .right = rm.transformDirection(.{ .x = 1, .y = 0, .z = 0 }),
        .up = rm.transformDirection(.{ .x = 0, .y = 1, .z = 0 }),
    };
}

/// Apply this frame's navigation. Returns true if the camera moved/looked, so
/// the viewport knows navigation is active.
pub fn navigate(nav: Nav) bool {
    var active = false;
    const b = basis();

    if (nav.rmb_down) {
        if (nav.look_dx != 0 or nav.look_dy != 0) {
            yaw += nav.look_dx * look_sensitivity;
            pitch += nav.look_dy * look_sensitivity;
            pitch = std.math.clamp(pitch, -89, 89);
            active = true;
        }
        var dir = Vector3{};
        if (nav.forward) dir = dir.add(b.fwd);
        if (nav.back) dir = dir.subtract(b.fwd);
        if (nav.right) dir = dir.add(b.right);
        if (nav.left) dir = dir.subtract(b.right);
        if (nav.up) dir = dir.add(.{ .x = 0, .y = 1, .z = 0 });
        if (nav.down) dir = dir.subtract(.{ .x = 0, .y = 1, .z = 0 });
        if (dir.lengthSquared() > 1e-6) {
            const speed = if (nav.fast) move_speed * FAST_MULTIPLIER else move_speed;
            pos = pos.add(dir.normalize().scale(speed * nav.dt));
            active = true;
        }
    }

    if (nav.pan_dx != 0 or nav.pan_dy != 0) {
        // Screen-space drag pan: dragging right/down moves the view (and so the
        // camera) right/down, i.e. the camera translates opposite the drag.
        pos = pos.subtract(b.right.scale(nav.pan_dx * pan_speed));
        pos = pos.add(b.up.scale(nav.pan_dy * pan_speed));
        active = true;
    }

    if (nav.wheel != 0) {
        pos = pos.add(b.fwd.scale(nav.wheel * zoom_speed));
        active = true;
    }
    return active;
}

/// Frame the camera on `target`, keeping the current view direction and backing
/// off by `dist`. Used by "focus on selection" (F key).
pub fn focusOn(target: Vector3, dist: f32) void {
    initialized = true;
    const b = basis();
    pos = target.subtract(b.fwd.scale(@max(dist, 1.0)));
}

/// Sets yaw/pitch directly and repositions the camera to keep `focus` at
/// `dist` along the new view direction. Used by the axis-orientation gizmo to
/// snap to a world axis without losing the current focus point.
pub fn snapTo(new_yaw: f32, new_pitch: f32, focus: Vector3, dist: f32) void {
    initialized = true;
    yaw = new_yaw;
    pitch = new_pitch;
    pos = focus.subtract(basis().fwd.scale(@max(dist, 0.01)));
}
