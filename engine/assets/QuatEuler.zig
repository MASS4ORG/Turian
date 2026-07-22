const std = @import("std");

/// Converts a quaternion (xyzw) to Euler degrees matching the engine's
/// rotation convention (`Transform.rotation` / `Matrix4.rotationEuler`'s
/// `Ry(yaw)*Rx(pitch)*Rz(roll)`). Shared by `GltfHierarchy.zig`/
/// `FbxHierarchy.zig` (both decompose a loader-native quaternion into the
/// same node-hierarchy `SceneNode.transform`); deliberately duplicated rather
/// than reused from `studio/inspector/PropDrawMath.zig` — `editor`/`engine`
/// cannot depend on the `studio` GUI layer.
pub fn quatToEulerDeg(q: [4]f32) [3]f32 {
    const x = q[0];
    const y = q[1];
    const z = q[2];
    const w = q[3];

    const sinr = 2.0 * (w * x + y * z);
    const cosr = 1.0 - 2.0 * (x * x + y * y);
    const pitch = std.math.atan2(sinr, cosr) * (180.0 / std.math.pi);

    const sinp = 2.0 * (w * y - z * x);
    const yaw = if (@abs(sinp) >= 1.0)
        std.math.copysign(@as(f32, 90.0), sinp)
    else
        std.math.asin(sinp) * (180.0 / std.math.pi);

    const siny = 2.0 * (w * z + x * y);
    const cosy = 1.0 - 2.0 * (y * y + z * z);
    const roll = std.math.atan2(siny, cosy) * (180.0 / std.math.pi);

    return .{ pitch, yaw, roll };
}
