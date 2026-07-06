//! Minimal cross-cutting application-control service (Unity's `Application.Quit`
//! analogue). The host loop (generated game `main`) owns the instance and
//! registers it into `Services`, so both SceneNode script components and UI
//! button click handlers can request an orderly shutdown the same way they
//! reach any other service: `frame.service(engine.Application).?.quit()`.

pub const Application = struct {
    /// Set by `quit()`; the host loop checks this once per frame and breaks
    /// out of the main loop when true. Never cleared — quitting is one-way.
    quit_requested: bool = false,

    /// Request the host loop stop after the current frame. Idempotent.
    pub fn quit(self: *Application) void {
        self.quit_requested = true;
    }
};

test "quit sets quit_requested" {
    const std = @import("std");
    var app = Application{};
    try std.testing.expect(!app.quit_requested);
    app.quit();
    try std.testing.expect(app.quit_requested);
}
