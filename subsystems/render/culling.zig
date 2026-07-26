//! View-frustum culling for the scene renderer. Pure math, no GPU/GUI deps —
//! given a camera's view-projection matrix and an object's local-space AABB
//! plus model matrix, decides whether the object can possibly be visible.
const engine = @import("engine");

const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

/// A frustum half-space plane, `a*x + b*y + c*z + d`: positive on the inside.
const Plane = struct {
    a: f32,
    b: f32,
    c: f32,
    d: f32,
};

/// The 6 half-space planes bounding a camera's view volume, in world space.
pub const Frustum = struct {
    planes: [6]Plane,

    /// Extracts the 6 frustum planes from a combined view-projection matrix
    /// (Gribb-Hartmann method). `vp` must map world space to OpenGL-style clip
    /// space (`x`/`y`/`z` each in `[-w, w]`), which is what `Matrix4.perspective`
    /// and `.orthographic` produce.
    pub fn extract(vp: Matrix4) Frustum {
        const m = vp.m;
        // row(i) in this column-major layout is {m[i], m[4+i], m[8+i], m[12+i]}.
        const r0: [4]f32 = .{ m[0], m[4], m[8], m[12] };
        const r1: [4]f32 = .{ m[1], m[5], m[9], m[13] };
        const r2: [4]f32 = .{ m[2], m[6], m[10], m[14] };
        const r3: [4]f32 = .{ m[3], m[7], m[11], m[15] };
        return .{
            .planes = .{
                addRows(r3, r0), // left:   w + x >= 0
                subRows(r3, r0), // right:  w - x >= 0
                addRows(r3, r1), // bottom: w + y >= 0
                subRows(r3, r1), // top:    w - y >= 0
                addRows(r3, r2), // near:   w + z >= 0
                subRows(r3, r2), // far:    w - z >= 0
            },
        };
    }

    fn addRows(x: [4]f32, y: [4]f32) Plane {
        return .{ .a = x[0] + y[0], .b = x[1] + y[1], .c = x[2] + y[2], .d = x[3] + y[3] };
    }

    fn subRows(x: [4]f32, y: [4]f32) Plane {
        return .{ .a = x[0] - y[0], .b = x[1] - y[1], .c = x[2] - y[2], .d = x[3] - y[3] };
    }
};

/// A world-space axis-aligned box, as center + half-extents.
pub const WorldAabb = struct { center: Vector3, extent: Vector3 };

/// The world-space AABB of the local box `[local_min, local_max]` under `model`.
/// Uses the standard center/extents transform, so rotated/scaled bounds stay a
/// conservative (never-too-small) box.
pub fn worldAabb(local_min: [3]f32, local_max: [3]f32, model: Matrix4) WorldAabb {
    const center_local = Vector3{
        .x = (local_min[0] + local_max[0]) * 0.5,
        .y = (local_min[1] + local_max[1]) * 0.5,
        .z = (local_min[2] + local_max[2]) * 0.5,
    };
    const extent_local = Vector3{
        .x = (local_max[0] - local_min[0]) * 0.5,
        .y = (local_max[1] - local_min[1]) * 0.5,
        .z = (local_max[2] - local_min[2]) * 0.5,
    };

    const m = model.m;
    return .{
        .center = model.transformPoint(center_local),
        .extent = .{
            .x = @abs(m[0]) * extent_local.x + @abs(m[4]) * extent_local.y + @abs(m[8]) * extent_local.z,
            .y = @abs(m[1]) * extent_local.x + @abs(m[5]) * extent_local.y + @abs(m[9]) * extent_local.z,
            .z = @abs(m[2]) * extent_local.x + @abs(m[6]) * extent_local.y + @abs(m[10]) * extent_local.z,
        },
    };
}

/// True if the world-space AABB of `[local_min, local_max]` transformed by
/// `model` is entirely outside `frustum` (safe to skip drawing). Uses the
/// "positive vertex" test against each plane.
pub fn aabbOutsideFrustum(local_min: [3]f32, local_max: [3]f32, model: Matrix4, frustum: Frustum) bool {
    const box = worldAabb(local_min, local_max, model);
    const center = box.center;
    const extent = box.extent;

    for (frustum.planes) |p| {
        const dist = p.a * center.x + p.b * center.y + p.c * center.z + p.d;
        const radius = @abs(p.a) * extent.x + @abs(p.b) * extent.y + @abs(p.c) * extent.z;
        if (dist + radius < 0) return true;
    }
    return false;
}

const std = @import("std");

test "aabbOutsideFrustum: object dead ahead of camera is inside" {
    const proj = Matrix4.perspective(60.0, 1.0, 0.1, 1000.0);
    const view = Matrix4.lookAt(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 0, .y = 0, .z = 1 }, .{ .x = 0, .y = 1, .z = 0 });
    const frustum = Frustum.extract(proj.multiply(view));

    const outside = aabbOutsideFrustum(.{ -1, -1, -1 }, .{ 1, 1, 1 }, Matrix4.translation(0, 0, 10), frustum);
    try std.testing.expect(!outside);
}

test "aabbOutsideFrustum: object far to the side is outside" {
    const proj = Matrix4.perspective(60.0, 1.0, 0.1, 1000.0);
    const view = Matrix4.lookAt(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 0, .y = 0, .z = 1 }, .{ .x = 0, .y = 1, .z = 0 });
    const frustum = Frustum.extract(proj.multiply(view));

    const outside = aabbOutsideFrustum(.{ -1, -1, -1 }, .{ 1, 1, 1 }, Matrix4.translation(500, 0, 10), frustum);
    try std.testing.expect(outside);
}

test "aabbOutsideFrustum: object behind the camera is outside" {
    const proj = Matrix4.perspective(60.0, 1.0, 0.1, 1000.0);
    const view = Matrix4.lookAt(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 0, .y = 0, .z = 1 }, .{ .x = 0, .y = 1, .z = 0 });
    const frustum = Frustum.extract(proj.multiply(view));

    const outside = aabbOutsideFrustum(.{ -1, -1, -1 }, .{ 1, 1, 1 }, Matrix4.translation(0, 0, -10), frustum);
    try std.testing.expect(outside);
}

test "aabbOutsideFrustum: large object straddling the frustum boundary is inside" {
    const proj = Matrix4.perspective(60.0, 1.0, 0.1, 1000.0);
    const view = Matrix4.lookAt(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 0, .y = 0, .z = 1 }, .{ .x = 0, .y = 1, .z = 0 });
    const frustum = Frustum.extract(proj.multiply(view));

    // Centered far outside to the side, but large enough to still clip into view.
    const outside = aabbOutsideFrustum(.{ -1000, -1, -1 }, .{ 1000, 1, 1 }, Matrix4.translation(0, 0, 10), frustum);
    try std.testing.expect(!outside);
}

test "worldAabb: translation moves the center and leaves the extent alone" {
    const box = worldAabb(.{ -1, -2, -3 }, .{ 1, 2, 3 }, Matrix4.translation(10, 20, 30));
    try std.testing.expectApproxEqAbs(@as(f32, 10), box.center.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 20), box.center.y, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 30), box.center.z, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 1), box.extent.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), box.extent.y, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 3), box.extent.z, 1e-4);
}

test "worldAabb: an off-center local box keeps its offset after transform" {
    // A mesh authored away from its origin — the case the shadow fit depends on,
    // since one big mesh at the origin carries all its extent in these bounds.
    const box = worldAabb(.{ 10, 0, 10 }, .{ 30, 4, 30 }, Matrix4.identity);
    try std.testing.expectApproxEqAbs(@as(f32, 20), box.center.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), box.center.y, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 10), box.extent.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), box.extent.y, 1e-4);
}

test "worldAabb: scale multiplies the extent" {
    const box = worldAabb(.{ -1, -1, -1 }, .{ 1, 1, 1 }, Matrix4.scaling(2, 3, 4));
    try std.testing.expectApproxEqAbs(@as(f32, 2), box.extent.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 3), box.extent.y, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 4), box.extent.z, 1e-4);
}

test "worldAabb: rotating 45 degrees grows the axis-aligned extent" {
    const box = worldAabb(.{ -1, -1, -1 }, .{ 1, 1, 1 }, Matrix4.rotationEuler(0, 45, 0));
    // A unit cube spun 45 degrees about Y spans sqrt(2) on X and Z.
    try std.testing.expectApproxEqAbs(@as(f32, @sqrt(2.0)), box.extent.x, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 1), box.extent.y, 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, @sqrt(2.0)), box.extent.z, 1e-4);
}
