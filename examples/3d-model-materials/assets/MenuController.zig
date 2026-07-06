const engine = @import("engine");

/// Wires the "Quit" button (see `assets/ui.uidoc`) to a real shutdown via the
/// `Application` service (`engine.Frame.service(engine.Application)`),
/// reachable from UI button handlers the same way it's reachable from any
/// SceneNode script's lifecycle hooks.
pub const QuitClicked = struct {
    pub const event_name = "quit_clicked";
};

/// A file-scope global, not `self` — `UiEvents.on`'s `ctx` pointer must
/// outlive the registration, but a live script component's storage can be
/// swap-compacted by prefab/spawn churn elsewhere in a scene. See the same
/// note in `examples/basic-project/assets/MenuController.zig`.
var g_app: ?*engine.Application = null;

pub const MenuController = struct {
    pub const is_component = true;

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        _ = self;
        g_app = frame.service(engine.Application);
        const ev = frame.service(engine.ui.UiEvents) orelse return;
        ev.on(QuitClicked, &g_app, onQuit);
    }

    fn onQuit(ctx: *?*engine.Application, _: QuitClicked) void {
        if (ctx.*) |app| app.quit();
    }
};
