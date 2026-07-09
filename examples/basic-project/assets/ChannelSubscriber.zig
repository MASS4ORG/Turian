const std = @import("std");
const engine = @import("engine");

/// #41/#107 showcase: a GameEvent channel decouples a UI button ("Door" in
/// ui.uidoc, bound via `on_click: {"channel": "<door-channel.asset GUID>"}")
/// from this subscriber — neither references the other, only the shared
/// asset GUID. Compare to `JumpOnClick`/`MenuController`'s `named` events:
/// same idea, but the publisher is Inspector-wired to an asset instead of a
/// hand-typed string that has to match a `pub const event_name`.
var g_ctx: u8 = 0;

fn onDoorChannel(_: *u8) void {
    std.debug.print("[ChannelSubscriber] door channel raised\n", .{});
}

pub const ChannelSubscriber = struct {
    pub const is_component = true;

    channel: engine.TypedAssetRef(.game_event) = .{},

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        const ev = frame.gameEvent(self.channel) orelse return;
        ev.on(&g_ctx, onDoorChannel);
    }
};
