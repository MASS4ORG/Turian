const std = @import("std");
const engine = @import("engine");

pub const Rotator = struct {
    pub const is_component = true;

    /// Degrees per second.
    speed: f32 = 45.0,
    /// World-space axis to rotate around (should be a unit vector).
    axis: engine.Vector3 = .{ .x = 0, .y = 1, .z = 0 },

    pub fn update(self: *@This(), transform: *engine.Transform, _: []engine.SceneNode, time: engine.Time) void {
        transform.rotation.x = @mod(transform.rotation.x + self.axis.x * self.speed * time.delta, 360.0);
        transform.rotation.y = @mod(transform.rotation.y + self.axis.y * self.speed * time.delta, 360.0);
        transform.rotation.z = @mod(transform.rotation.z + self.axis.z * self.speed * time.delta, 360.0);
    }
};
