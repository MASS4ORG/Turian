const std = @import("std");
const engine = @import("engine");

const log = std.log.scoped(.menu_controller);

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
/// so it anchors to something that never moves instead. Also caches the
/// `Application` service pointer from `awake`'s `Frame` — `UiEvents.on`
/// handlers aren't passed a `Frame` themselves, and `Application`'s host
/// instance is stable for the whole session (registered once into
/// `Services`), so stashing it here is safe.
const MenuCtx = struct { app: ?*engine.Application = null };
var g_menu_ctx: MenuCtx = .{};

pub const MenuController = struct {
    pub const is_component = true;

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        _ = self;
        g_menu_ctx.app = frame.service(engine.Application);
        const ev = frame.service(engine.ui.UiEvents) orelse return;
        ev.on(PlayClicked, &g_menu_ctx, onPlay);
        ev.on(QuitClicked, &g_menu_ctx, onQuit);
    }

    fn onPlay(_: *MenuCtx, _: PlayClicked) void {
        log.info("Play clicked", .{});
    }

    fn onQuit(ctx: *MenuCtx, _: QuitClicked) void {
        log.info("Quit clicked", .{});
        if (ctx.app) |app| app.quit();
    }
};
