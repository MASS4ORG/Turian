const Vector3 = @import("../root.zig").Vector3;

/// Local position, rotation (Euler angles), and scale of a game object.
pub const Transform = struct {
    /// Local position in world units.
    position: Vector3 = .{},
    /// Euler rotation in degrees.
    rotation: Vector3 = .{},
    /// Local scale factor.
    scale: Vector3 = .{ .x = 1, .y = 1, .z = 1 },
};
