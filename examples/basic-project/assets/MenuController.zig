const std = @import("std");
const engine = @import("engine");

/// M3.12 demo: wires the announcement demo's Play/Quit buttons to real game
/// code via typed events (D4) — not just names that resolve, actual handlers
/// that run.
pub const PlayClicked = struct {
    pub const event_name = "play_clicked";
};
pub const QuitClicked = struct {
    pub const event_name = "quit_clicked";
};

/// A file-scope global, not `self` — `UiEvents.on`'s `ctx` pointer must
/// outlive the registration, but a live script component's storage
/// (`g_live[]`) gets swap-compacted when an *earlier* component is
/// destroyed (see `Spawner`-driven prefab churn in this same scene),
/// which would silently invalidate a `self` pointer registered here.
/// Every other cross-script reference in this codebase resolves a GUID
/// fresh each frame for the same reason; a callback context can't do that,
/// so it anchors to something that never moves instead.
var g_menu_ctx: u8 = 0;

pub const MenuController = struct {
    pub const is_component = true;

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        _ = self;
        const ev = frame.service(engine.ui.UiEvents) orelse return;
        ev.on(PlayClicked, &g_menu_ctx, onPlay);
        ev.on(QuitClicked, &g_menu_ctx, onQuit);
    }

    fn onPlay(_: *u8, _: PlayClicked) void {
        std.debug.print("[MenuController] Play clicked\n", .{});
    }

    fn onQuit(_: *u8, _: QuitClicked) void {
        std.debug.print("[MenuController] Quit clicked\n", .{});
    }
};
