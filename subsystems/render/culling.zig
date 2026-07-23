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

/// True if the world-space AABB of `[local_min, local_max]` transformed by
/// `model` is entirely outside `frustum` (safe to skip drawing). Uses the
/// standard center/extents + "positive vertex" test, so rotated/scaled
/// bounds stay a conservative (never-too-small) box.
pub fn aabbOutsideFrustum(local_min: [3]f32, local_max: [3]f32, model: Matrix4, frustum: Frustum) bool {
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

    const center = model.transformPoint(center_local);
    const m = model.m;
    const extent = Vector3{
        .x = @abs(m[0]) * extent_local.x + @abs(m[4]) * extent_local.y + @abs(m[8]) * extent_local.z,
        .y = @abs(m[1]) * extent_local.x + @abs(m[5]) * extent_local.y + @abs(m[9]) * extent_local.z,
        .z = @abs(m[2]) * extent_local.x + @abs(m[6]) * extent_local.y + @abs(m[10]) * extent_local.z,
    };

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
