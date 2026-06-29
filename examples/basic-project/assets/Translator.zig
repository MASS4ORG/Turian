const std = @import("std");
const engine = @import("engine");

pub const Translator = struct {
    pub const is_component = true;

    target: engine.GameObjectRef = .{},
    mesh: engine.AssetRef = .{},
    /// Maximum displacement in metres.
    amplitude: f32 = 2.0,
    /// Oscillations per second.
    frequency: f32 = 0.5,

    _elapsed: f32 = 0,

    pub fn update(self: *@This(), time: engine.Time) void {
        self._elapsed += time.delta;

        const offset_y = self.amplitude * @sin(2.0 * std.math.pi * self.frequency * self._elapsed);

        // TODO: apply offset_y to the parent Transform once the
        // scene-mutation API is available.
        _ = offset_y;

        if (@mod(self._elapsed, 1.0) < time.delta) {
            std.debug.print("[Translator] t={d:.1}s  offset_y={d:.3}m\n", .{
                self._elapsed, self.amplitude * @sin(2.0 * std.math.pi * self.frequency * self._elapsed),
            });
        }
    }
};
