//! World→viewport projection (C5/C8): delegates to the `math3d` package.
//! Thin wrappers adapt the math-3d Y-up screen convention to the engine's
//! Y-down convention for GUI-relative coordinates.

const std = @import("std");
const math = @import("math");

/// Perspective view*projection for a camera at `pos`/`rotation_euler` (engine
/// rotation convention: `Matrix4.rotationEuler(x,y,z).transformDirection(0,0,1)`
/// is forward).
pub const cameraViewProj = math.Projection.cameraViewProj;

/// Project `world` through `view_proj` into `viewport` (x, y, w, h) pixel
/// coordinates, y-down (GUI convention). Returns null when the point is behind
/// the camera plane.
pub fn worldToViewport(view_proj: math.Matrix4, viewport: [4]f32, world: math.Vector3) ?[2]f32 {
    const r = math.Projection.worldToScreen(view_proj, viewport, world) orelse return null;
    // Flip Y: math-3d returns Y-up; the engine's GUI uses Y-down.
    return .{ r[0], viewport[1] + viewport[3] - (r[1] - viewport[1]) };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "a point straight ahead of the camera projects to the viewport center" {
    const view = math.Matrix4.lookAt(
        .{ .x = 0, .y = 0, .z = 0 },
        .{ .x = 0, .y = 0, .z = 1 },
        .{ .x = 0, .y = 1, .z = 0 },
    );
    const proj = math.Matrix4.perspective(60.0, 16.0 / 9.0, 0.01, 100.0);
    const vp = proj.multiply(view);

    const p = worldToViewport(vp, .{ 0, 0, 1920, 1080 }, .{ .x = 0, .y = 0, .z = 5 }) orelse
        return error.UnexpectedlyBehindCamera;
    try std.testing.expectApproxEqAbs(@as(f32, 960), p[0], 0.5);
    try std.testing.expectApproxEqAbs(@as(f32, 540), p[1], 0.5);
}

test "a point behind the camera returns null" {
    const view = math.Matrix4.lookAt(
        .{ .x = 0, .y = 0, .z = 0 },
        .{ .x = 0, .y = 0, .z = 1 },
        .{ .x = 0, .y = 1, .z = 0 },
    );
    const proj = math.Matrix4.perspective(60.0, 16.0 / 9.0, 0.01, 100.0);
    const vp = proj.multiply(view);

    try std.testing.expectEqual(
        @as(?[2]f32, null),
        worldToViewport(vp, .{ 0, 0, 1920, 1080 }, .{ .x = 0, .y = 0, .z = -5 }),
    );
}

test "a point above the view axis lands in the upper half (y-down)" {
    const view = math.Matrix4.lookAt(
        .{ .x = 0, .y = 0, .z = 0 },
        .{ .x = 0, .y = 0, .z = 1 },
        .{ .x = 0, .y = 1, .z = 0 },
    );
    const proj = math.Matrix4.perspective(60.0, 16.0 / 9.0, 0.01, 100.0);
    const vp = proj.multiply(view);

    const p = worldToViewport(vp, .{ 0, 0, 1920, 1080 }, .{ .x = 0, .y = 1, .z = 5 }) orelse
        return error.UnexpectedlyBehindCamera;
    try std.testing.expect(p[1] < 540);
}
