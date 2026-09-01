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
/// Dolly per wheel notch as a *fraction* of `focus_dist`, not a world distance:
/// a notch has to be a nudge at arm's length from a prop and a stride when the
/// whole street is in frame, and no fixed world step is both.
pub var zoom_fraction: f32 = 0.15;
/// Multiplier on screen-accurate panning. 1.0 keeps the grabbed point under the
/// cursor; the knob exists only for taste.
pub var pan_scale: f32 = 1.0;

/// Shift multiplier applied to `move_speed` for fast travel.
const FAST_MULTIPLIER: f32 = 3.5;

/// Floor on `focus_dist`. Zoom is proportional to it, so at zero the camera
/// would freeze in place and never pull back out.
const MIN_FOCUS_DIST: f32 = 0.05;

/// Distance ahead of the camera that zoom and pan calibrate against — what the
/// view is treated as looking *at*. Maintained by `focusOn`/`snapTo` and by any
/// motion along the view axis, so flying up to a wall leaves zoom scaled to the
/// wall rather than to wherever the camera started.
var focus_dist: f32 = 6.0;

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
    /// Viewport height in pixels. Pan converts a pixel drag into world units
    /// through the projection, so it needs the surface it was measured on.
    viewport_height: f32 = 0,
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
    focus_dist: f32 = 6.0,
};

pub fn getState() State {
    return .{ .pos = pos, .yaw = yaw, .pitch = pitch, .fov = fov, .initialized = initialized, .focus_dist = focus_dist };
}

/// A partial pose queued from outside the draw loop, by the debug protocol's
/// `camera.set`. Each field is optional so a caller can move the camera without
/// also having to restate the fields it doesn't care about.
pub const Override = struct {
    pos: ?Vector3 = null,
    yaw: ?f32 = null,
    pitch: ?f32 = null,
    fov: ?f32 = null,
};

var pending_override: ?Override = null;

pub fn queueOverride(o: Override) void {
    pending_override = o;
}

/// Apply and clear a queued `Override`. The Scene viewport calls this *after*
/// swapping in its per-instance pose, since applying it any earlier would be
/// undone by that swap — each Scene dock tab owns its own camera and restores it
/// at the top of every draw.
pub fn takeOverride() void {
    const o = pending_override orelse return;
    pending_override = null;
    if (o.pos) |v| pos = v;
    if (o.yaw) |v| yaw = v;
    if (o.pitch) |v| pitch = v;
    if (o.fov) |v| fov = v;
    // Suppress `ensureInit`'s seed-from-scene-camera, which would otherwise
    // overwrite an override applied to a tab that hasn't drawn yet.
    initialized = true;
}

/// Snap the free-look camera onto `obj`'s Camera component pose, so the Scene
/// view shows what that camera sees. Returns false if `obj` has no camera.
pub fn alignToCameraNode(obj: *const engine.SceneNode) bool {
    for (obj.components[0..obj.component_count]) |*comp| {
        if (comp.* != .camera) continue;
        queueOverride(.{
            .pos = obj.transform.position,
            .pitch = obj.transform.rotation.x,
            .yaw = obj.transform.rotation.y,
            .fov = comp.camera.fov,
        });
        return true;
    }
    return false;
}

pub fn setState(s: State) void {
    pos = s.pos;
    yaw = s.yaw;
    pitch = s.pitch;
    fov = s.fov;
    initialized = s.initialized;
    focus_dist = @max(MIN_FOCUS_DIST, s.focus_dist);
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
            const delta = dir.normalize().scale(speed * nav.dt);
            pos = pos.add(delta);
            // Keep the zoom/pan calibration honest after a flythrough: closing on
            // a wall with W has to leave the wheel scaled to the wall.
            const along = delta.x * b.fwd.x + delta.y * b.fwd.y + delta.z * b.fwd.z;
            focus_dist = @max(MIN_FOCUS_DIST, focus_dist - along);
            active = true;
        }
    }

    if (nav.pan_dx != 0 or nav.pan_dy != 0) {
        // Screen-space drag pan: dragging right/down moves the view (and so the
        // camera) right/down, i.e. the camera translates opposite the drag.
        const wpp = worldPerPixel(nav.viewport_height) * pan_scale;
        pos = pos.subtract(b.right.scale(nav.pan_dx * wpp));
        pos = pos.add(b.up.scale(nav.pan_dy * wpp));
        active = true;
    }

    if (nav.wheel != 0) {
        // Proportional dolly: each notch covers a fixed share of the remaining
        // distance, so approaching decelerates and can never reach the focus
        // point, let alone pass through it.
        const want = focus_dist * zoom_fraction * nav.wheel;
        const step = @min(want, focus_dist - MIN_FOCUS_DIST);
        pos = pos.add(b.fwd.scale(step));
        focus_dist = @max(MIN_FOCUS_DIST, focus_dist - step);
        active = true;
    }
    return active;
}

/// World units one screen pixel spans at the focus plane. Panning by this keeps
/// the grabbed point under the cursor at any distance, which is what makes a
/// single `pan_scale` work from a doorknob to a city block.
fn worldPerPixel(viewport_h: f32) f32 {
    if (viewport_h <= 0) return 0;
    const half_fov = fov * 0.5 * std.math.pi / 180.0;
    return 2.0 * focus_dist * @tan(half_fov) / viewport_h;
}

/// Frame the camera on `target`, keeping the current view direction and backing
/// off by `dist`. Used by "focus on selection" (F key).
pub fn focusOn(target: Vector3, dist: f32) void {
    initialized = true;
    const b = basis();
    focus_dist = @max(dist, 1.0);
    pos = target.subtract(b.fwd.scale(focus_dist));
}

/// Sets yaw/pitch directly and repositions the camera to keep `focus` at
/// `dist` along the new view direction. Used by the axis-orientation gizmo to
/// snap to a world axis without losing the current focus point.
pub fn snapTo(new_yaw: f32, new_pitch: f32, focus: Vector3, dist: f32) void {
    initialized = true;
    yaw = new_yaw;
    pitch = new_pitch;
    focus_dist = @max(dist, MIN_FOCUS_DIST);
    pos = focus.subtract(basis().fwd.scale(focus_dist));
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// Puts the camera at the origin looking down +Z with a known focus distance.
fn testReset(dist: f32) void {
    setState(.{ .pos = .{ .x = 0, .y = 0, .z = 0 }, .yaw = 0, .pitch = 0, .fov = 60, .initialized = true, .focus_dist = dist });
    zoom_fraction = 0.15;
    pan_scale = 1.0;
}

test "a wheel notch covers a fixed share of the distance, not a fixed distance" {
    // The whole point of #150: one notch has to be a nudge up close and a
    // stride far away, which a world-unit step cannot be.
    testReset(1.0);
    _ = navigate(.{ .wheel = 1 });
    const near_step = getState().pos.z;

    testReset(100.0);
    _ = navigate(.{ .wheel = 1 });
    const far_step = getState().pos.z;

    try std.testing.expectApproxEqRel(@as(f32, 100.0), far_step / near_step, 1e-3);
}

test "zooming in decelerates and never reaches the focus point" {
    testReset(10.0);
    // Far more notches than it would take at a linear rate to cross 10 units.
    for (0..500) |_| _ = navigate(.{ .wheel = 1 });
    const travelled = getState().pos.z;
    try std.testing.expect(travelled < 10.0);
    try std.testing.expect(getState().focus_dist >= MIN_FOCUS_DIST);
}

test "zooming back out is unbounded" {
    testReset(1.0);
    for (0..50) |_| _ = navigate(.{ .wheel = -1 });
    try std.testing.expect(getState().pos.z < -10.0);
    try std.testing.expect(getState().focus_dist > 100.0);
}

test "focusOn recalibrates the wheel to the framed object" {
    testReset(500.0);
    focusOn(.{ .x = 0, .y = 0, .z = 20 }, 4.0);
    try std.testing.expectApproxEqAbs(@as(f32, 4.0), getState().focus_dist, 1e-4);
    // A notch is now scaled to 4 units away, not to the 500 it was before.
    const before = getState().pos.z;
    _ = navigate(.{ .wheel = 1 });
    try std.testing.expectApproxEqAbs(@as(f32, 4.0 * 0.15), getState().pos.z - before, 1e-4);
}

test "a drag of half the viewport height pans one focus-plane half-height" {
    // Screen-accurate pan: the grabbed point stays under the cursor, which is
    // what lets one `pan_scale` serve every distance.
    testReset(10.0);
    const h: f32 = 720;
    _ = navigate(.{ .pan_dy = h / 2, .viewport_height = h });
    const expect = 10.0 * @tan(@as(f32, 30.0) * std.math.pi / 180.0);
    try std.testing.expectApproxEqRel(expect, getState().pos.y, 1e-4);
}

test "flying forward leaves the wheel scaled to what is now in front" {
    testReset(10.0);
    _ = navigate(.{ .rmb_down = true, .forward = true, .dt = 1.0 });
    // move_speed is 4 world units/second, all of it along the view axis.
    try std.testing.expectApproxEqAbs(@as(f32, 6.0), getState().focus_dist, 1e-4);
}
