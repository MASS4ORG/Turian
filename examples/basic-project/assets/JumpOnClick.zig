const std = @import("std");
const engine = @import("engine");

/// GUI-affects-world demo (#47 showcase): the "Jump!" button in `ui.uidoc`
/// fires this typed event; `JumpOnClick.update` drives a simple parabolic hop
/// on `target`'s Transform in response — the mirror direction of
/// `WorldMarker`/`HealthHud` (world -> GUI).
pub const JumpClicked = struct {
    pub const event_name = "jump_clicked";
};

/// File-scope, not `self`: `UiEvents.on`'s `ctx` pointer must outlive the
/// registration, but a live component's storage can be swap-compacted by
/// other prefab churn in this scene (see `MenuController`'s identical note).
/// `update` itself always runs with a freshly-resolved `self`, so the jump
/// state kept on the component below is safe — only the *ctx pointer handed
/// to `UiEvents` once at awake* needs to be this stable instead.
var g_jump_requested: bool = false;
var g_click_ctx: u8 = 0;

fn onJumpClicked(_: *u8, _: JumpClicked) void {
    g_jump_requested = true;
}

pub const JumpOnClick = struct {
    pub const is_component = true;

    /// Stable GUID of the target SceneNode to hop.
    target: engine.GameObjectRef = .{},
    /// Peak height above the target's resting Y, in metres.
    height: f32 = 1.5,
    /// Hop duration in seconds.
    duration: f32 = 0.6,

    _jumping: bool = false,
    _t: f32 = 0,
    _base_y: f32 = 0,

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        _ = self;
        const ev = frame.service(engine.ui.UiEvents) orelse return;
        ev.on(JumpClicked, &g_click_ctx, onJumpClicked);
    }

    pub fn update(self: *@This(), _: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) void {
        const guid = self.target.slice();
        if (guid.len == 0) return;
        for (objects) |*obj| {
            if (!obj.active or !std.mem.eql(u8, obj.guidSlice(), guid)) continue;

            if (g_jump_requested and !self._jumping) {
                g_jump_requested = false;
                self._jumping = true;
                self._t = 0;
                self._base_y = obj.transform.position.y;
            }
            if (self._jumping) {
                self._t += time.delta;
                if (self._t >= self.duration) {
                    self._jumping = false;
                    obj.transform.position.y = self._base_y;
                } else {
                    const p = self._t / self.duration;
                    obj.transform.position.y = self._base_y + self.height * @sin(std.math.pi * p);
                }
            }
            break;
        }
    }
};
