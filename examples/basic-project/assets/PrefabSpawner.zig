const std = @import("std");
const engine = @import("engine");

/// Demonstrates the runtime prefab API (issue #32). On a fixed interval it
/// `Instantiate`s a prefab; once a cap of live instances is reached it `Destroy`s
/// the oldest one. Every operation is logged to the console.
///
/// Assign `prefab` to a `.prefab` asset in the Inspector (e.g. Spinning_Cube),
/// then enter Play and watch instances appear and disappear.
pub const PrefabSpawner = struct {
    pub const is_component = true;

    /// Prefab to instantiate — drop a `.prefab` here in the Inspector.
    prefab: engine.TypedAssetRef(.scene) = .{},
    /// Seconds between each spawn / destroy step.
    interval: f32 = 1.0,
    /// Maximum live instances before the oldest is destroyed.
    max_alive: i32 = 5,

    _timer: f32 = 0,
    _spawn_count: u32 = 0,

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const guid = self.prefab.slice();
        if (guid.len == 0) return; // no prefab assigned

        self._timer += frame.time.delta;
        if (self._timer < self.interval) return;
        self._timer = 0;

        // Count live instances of this prefab and remember the first (oldest).
        var alive: i32 = 0;
        var oldest: ?*const engine.SceneNode = null;
        for (frame.objects) |*obj| {
            if (std.mem.eql(u8, obj.prefabSourceSlice(), guid)) {
                alive += 1;
                if (oldest == null) oldest = obj;
            }
        }

        if (alive >= self.max_alive) {
            if (oldest) |o| {
                std.debug.print("[PrefabSpawner] Destroy \"{s}\" (alive {d})\n", .{ o.nameSlice(), alive });
                frame.destroy(o); // Unity's Destroy(gameObject)
            }
        } else {
            // Spread spawns along X so they're easy to see.
            const lane: f32 = @floatFromInt(self._spawn_count % 5);
            const pos = engine.Vector3{ .x = lane - 2.0, .y = 1.0, .z = -2.0 };
            std.debug.print("[PrefabSpawner] Instantiate #{d} at x={d:.1} (alive {d})\n", .{ self._spawn_count, pos.x, alive });
            frame.instantiate(guid, pos, null); // Unity's Instantiate(prefab, pos, rot)
            self._spawn_count += 1;
        }
    }
};
