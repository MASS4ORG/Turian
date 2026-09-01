//! Minimal cross-cutting application-control service (Unity's `Application.Quit`
//! analogue). The host loop (generated game `main`) owns the instance and
//! registers it into `Services`, so both SceneNode script components and UI
//! button click handlers can request an orderly shutdown the same way they
//! reach any other service: `frame.service(engine.Application).?.quit()`.

pub const Application = struct {
    /// Set by `quit()`; the host loop checks this once per frame and breaks
    /// out of the main loop when true. Never cleared — quitting is one-way.
    quit_requested: bool = false,

    /// Frames per second the host loop holds itself to, or 0 to run as fast as
    /// presentation allows. Read once per frame, so a script or settings screen
    /// can change it live. A cap above the refresh rate does nothing useful —
    /// vsync is still the floor on presentation.
    frame_cap: u32 = 0,

    /// Request the host loop stop after the current frame. Idempotent.
    pub fn quit(self: *Application) void {
        self.quit_requested = true;
    }

    /// Nanoseconds one frame may take under the current cap, or 0 when uncapped.
    pub fn frameBudgetNs(self: *const Application) i96 {
        if (self.frame_cap == 0) return 0;
        return @divTrunc(1_000_000_000, @as(i96, self.frame_cap));
    }
};

test "frameBudgetNs converts a cap into a per-frame budget" {
    const std = @import("std");
    var app = Application{};
    try std.testing.expectEqual(@as(i96, 0), app.frameBudgetNs());
    app.frame_cap = 60;
    try std.testing.expectEqual(@as(i96, 16_666_666), app.frameBudgetNs());
    app.frame_cap = 30;
    try std.testing.expectEqual(@as(i96, 33_333_333), app.frameBudgetNs());
}

test "quit sets quit_requested" {
    const std = @import("std");
    var app = Application{};
    try std.testing.expect(!app.quit_requested);
    app.quit();
    try std.testing.expect(app.quit_requested);
}
