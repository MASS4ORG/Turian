const std = @import("std");
const engine = @import("engine");

/// C4 demo: drives a HUD label through the runtime document instance API
/// (`frame.uiDocument(node).setText`) instead of touching UI nodes directly.
/// Ticks a fake health value down so the label visibly changes in Play mode.
pub const HealthHud = struct {
    pub const is_component = true;

    /// The scene node carrying the `ui_document` component to update.
    hud: engine.GameObjectRef = .{},
    health: f32 = 100,
    drain_per_second: f32 = 5,

    _label: ?usize = null,

    fn resolveHud(self: *@This(), frame: engine.Frame) ?*engine.ui.UiInstance {
        const guid = self.hud.slice();
        if (guid.len == 0) return null;
        for (frame.objects) |*obj| {
            if (!std.mem.eql(u8, obj.guidSlice(), guid)) continue;
            return frame.uiDocument(obj);
        }
        return null;
    }

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const inst = self.resolveHud(frame) orelse return;
        if (self._label == null) self._label = inst.find("HealthLabel");
        const label = self._label orelse return;

        self.health = @max(0, self.health - self.drain_per_second * frame.time.delta);
        var buf: [32]u8 = undefined;
        const text = std.fmt.bufPrint(&buf, "HP: {d:.0}", .{self.health}) catch return;
        inst.setText(label, text);
    }
};
