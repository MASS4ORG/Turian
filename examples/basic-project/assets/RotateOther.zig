const std = @import("std");
const engine = @import("engine");

pub const RotateOther = struct {
    pub const is_component = true;

    /// Stable GUID of the target SceneNode to rotate.
    target: engine.GameObjectRef = .{},
    /// Degrees per second.
    speed: f32 = 90.0,
    /// World-space axis to rotate around (should be a unit vector).
    axis: engine.Vector3 = .{ .x = 0, .y = 1, .z = 0 },

    pub fn update(self: *@This(), _: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) void {
        const guid = self.target.slice();
        if (guid.len == 0) return;
        for (objects) |*obj| {
            if (!obj.active) continue;
            if (!std.mem.eql(u8, obj.guidSlice(), guid)) continue;
            obj.transform.rotation.x = @mod(obj.transform.rotation.x + self.axis.x * self.speed * time.delta, 360.0);
            obj.transform.rotation.y = @mod(obj.transform.rotation.y + self.axis.y * self.speed * time.delta, 360.0);
            obj.transform.rotation.z = @mod(obj.transform.rotation.z + self.axis.z * self.speed * time.delta, 360.0);
            break;
        }
    }
};
