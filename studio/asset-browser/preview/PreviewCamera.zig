//! Orbital camera used by the asset preview system: yaw/pitch/distance around a
//! fixed target (usually the origin, where a preview mesh is centered). Shared
//! by static thumbnail generation (a fixed, pleasant default angle) and the
//! interactive material-preview panel (drag to orbit, scroll to zoom).
const std = @import("std");
const engine = @import("engine");
const render = @import("render");

const Vector3 = engine.Vector3;
const Matrix4 = engine.Matrix4;

/// A pleasant default angle for static thumbnails — matches the "3/4 view"
/// convention most engines default asset previews to.
pub const default_yaw_deg: f32 = 35.0;
pub const default_pitch_deg: f32 = 20.0;

pub const Orbit = struct {
    /// Look-at target, world space.
    target: Vector3 = .{},
    /// Rotation around the world Y axis, degrees.
    yaw_deg: f32 = default_yaw_deg,
    /// Rotation above/below the horizontal plane, degrees. Clamped to avoid
    /// gimbal-flipping past the poles.
    pitch_deg: f32 = default_pitch_deg,
    /// Distance from `target`.
    distance: f32 = 2.5,
    fov: f32 = 40,
    /// Preview cameras project orthographically by default: a model framed
    /// close up under perspective foreshortens hard (a cube's near corner fans
    /// out and reads as broken geometry). Ortho keeps faces parallel and sizes
    /// true. `fov` still sets the framed extent (via `pose`) so switching to
    /// perspective needs no re-framing.
    orthographic: bool = true,
    /// Clip planes, kept proportional to `distance`/the framed radius (see
    /// `frame`) rather than fixed constants — a fixed near/far pair that
    /// happens to suit a small preview mesh will clip a large one (or vice
    /// versa), which showed up as wildly wrong-looking geometry for models
    /// far from the size `frame` was originally tuned against.
    near: f32 = 0.05,
    far: f32 = 500,
    /// The `distance` `frame()` last computed, before any interactive zoom —
    /// the reference scale `zoomBy` clamps around.
    frame_distance: f32 = 2.5,

    /// Drag the camera around `target` by a mouse delta (in pixels — the
    /// caller picks a sensitivity that feels right for its widget size).
    pub fn orbitBy(self: *Orbit, dx: f32, dy: f32, sensitivity: f32) void {
        self.yaw_deg -= dx * sensitivity;
        self.pitch_deg = std.math.clamp(self.pitch_deg - dy * sensitivity, -85.0, 85.0);
    }

    /// Zoom in/out (positive `delta` = closer). Clamped to a range scaled off
    /// the last `frame()` call's distance (not a fixed absolute range — a
    /// fixed [0.4, 50] range either traps a huge mesh's camera inside it or
    /// leaves a tiny mesh's camera unable to zoom in close enough).
    pub fn zoomBy(self: *Orbit, delta: f32) void {
        const min_d = @max(self.frame_distance * 0.05, 0.02);
        const max_d = @max(self.frame_distance * 20.0, 1.0);
        self.distance = std.math.clamp(self.distance - delta, min_d, max_d);
    }

    /// Frame `self` so a sphere of `radius` centered on `target` fits inside
    /// the view with a comfortable margin, computed from `fov` (not a flat
    /// distance multiplier — a fixed multiplier under-frames a wide FOV and
    /// over-frames a narrow one, which was clipping preview thumbnails).
    /// Also scales the clip planes to the object's actual size (see `near`/
    /// `far`'s doc comment). Called once when the previewed asset (or its
    /// bounds) changes.
    pub fn frame(self: *Orbit, target: Vector3, radius: f32) void {
        self.target = target;
        const r = @max(radius, 0.05);
        const fov_half_rad = self.fov * 0.5 * std.math.pi / 180.0;
        const fit_distance = r / @tan(fov_half_rad);
        self.distance = fit_distance * FRAME_MARGIN;
        self.frame_distance = self.distance;
        // Comfortably bracket the object regardless of its absolute scale:
        // near stays a small fraction of the viewing distance, far extends
        // well past the object's far side.
        self.near = @max(self.distance * 0.01, 0.001);
        self.far = self.distance + r * 6.0 + 10.0;
    }

    /// Multiplier applied on top of the exact-fit distance so the previewed
    /// object doesn't touch the frame edges.
    const FRAME_MARGIN: f32 = 1.4;

    /// Resolve to the render module's free-look camera pose. Uses the same
    /// `rotationEuler(pitch, yaw, 0)` convention as `EditorCamera`, so the
    /// forward direction is computed by the engine itself rather than
    /// re-derived — no risk of a sign/axis-order mismatch with the renderer.
    pub fn pose(self: Orbit) render.EditorCam {
        const rm = Matrix4.rotationEuler(self.pitch_deg, self.yaw_deg, 0);
        const fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
        const pos = Vector3{
            .x = self.target.x - fwd.x * self.distance,
            .y = self.target.y - fwd.y * self.distance,
            .z = self.target.z - fwd.z * self.distance,
        };
        // Ortho half-height tracks distance*tan(fov/2): at the framed distance
        // it equals the framed radius (so the object fits exactly as it would
        // under perspective), and interactive zoom (which changes `distance`)
        // scales the ortho view the same way it would scale a perspective one.
        const fov_half_rad = self.fov * 0.5 * std.math.pi / 180.0;
        const ortho_hh: f32 = if (self.orthographic) self.distance * @tan(fov_half_rad) else 0;
        return .{
            .pos = pos,
            .rot = .{ .x = self.pitch_deg, .y = self.yaw_deg, .z = 0 },
            .fov = self.fov,
            .near = self.near,
            .far = self.far,
            .ortho_half_height = ortho_hh,
        };
    }
};

test "pose faces the target from the configured distance" {
    var o = Orbit{ .yaw_deg = 0, .pitch_deg = 0, .distance = 3.0 };
    const cam = o.pose();
    // yaw=0,pitch=0 => camera sits on -Z looking toward +Z.
    try std.testing.expectApproxEqAbs(@as(f32, 0), cam.pos.x, 0.001);
    try std.testing.expectApproxEqAbs(@as(f32, 0), cam.pos.y, 0.001);
    try std.testing.expectApproxEqAbs(@as(f32, -3.0), cam.pos.z, 0.001);
}

test "frame sets an FOV-aware distance proportional to radius" {
    var o = Orbit{};
    o.frame(.{ .x = 1, .y = 2, .z = 3 }, 1.0);
    try std.testing.expectEqual(Vector3{ .x = 1, .y = 2, .z = 3 }, o.target);
    // radius 1.0 / tan(fov/2=20deg) * FRAME_MARGIN(1.4), fov defaults to 40deg.
    try std.testing.expectApproxEqAbs(@as(f32, 3.8465), o.distance, 0.001);
}

test "frame keeps a wider FOV closer than a narrower one for the same radius" {
    var wide = Orbit{ .fov = 90 };
    var narrow = Orbit{ .fov = 20 };
    wide.frame(.{}, 1.0);
    narrow.frame(.{}, 1.0);
    try std.testing.expect(wide.distance < narrow.distance);
}

test "zoomBy clamps relative to the last framed distance" {
    // frame_distance defaults to 2.5 -> min 0.125, max 50.
    var o = Orbit{ .distance = 1.0 };
    o.zoomBy(100.0);
    try std.testing.expectApproxEqAbs(@as(f32, 0.125), o.distance, 0.001);
    o.zoomBy(-1000.0);
    try std.testing.expectApproxEqAbs(@as(f32, 50.0), o.distance, 0.001);
}

test "frame scales near/far to the object's size instead of a fixed range" {
    var small = Orbit{};
    small.frame(.{}, 0.01); // tiny mesh
    try std.testing.expect(small.near < 0.01);
    try std.testing.expect(small.far > small.distance);

    var huge = Orbit{};
    huge.frame(.{}, 500.0); // building-scale mesh
    // The old fixed far=100 would have clipped this; it must not anymore.
    try std.testing.expect(huge.far > huge.distance + 500.0);
}

test "pitch clamps away from the poles" {
    var o = Orbit{};
    o.orbitBy(0, 10000, 1.0);
    try std.testing.expectApproxEqAbs(@as(f32, -85.0), o.pitch_deg, 0.001);
    o.orbitBy(0, -10000, 1.0);
    try std.testing.expectApproxEqAbs(@as(f32, 85.0), o.pitch_deg, 0.001);
}
