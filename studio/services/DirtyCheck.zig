//! Shared "you have unsaved changes" gate. Both quitting the app and
//! switching projects can discard open documents' edits, so they share one
//! dirty-check + confirmation dialog instead of duplicating the prompt.

const std = @import("std");
const gui = @import("gui");
const Documents = @import("../main-window/Documents.zig");
const ProjectOps = @import("ProjectOps.zig");
const StudioLocale = @import("StudioLocale.zig");
const tr = StudioLocale.tr;

const PendingAction = enum { none, quit, switch_project };

var pending: PendingAction = .none;
var quit_confirmed_flag = false;
var pending_path_buf: [1024]u8 = undefined;
var pending_path_len: usize = 0;

fn pendingPath() []const u8 {
    return pending_path_buf[0..pending_path_len];
}

/// True if any open document has unsaved changes.
pub fn hasUnsavedChanges() bool {
    return Documents.hasUnsavedChanges();
}

/// Request to quit the app. Confirms immediately if nothing is dirty;
/// otherwise defers behind the confirmation dialog. Poll `quitConfirmed`
/// once per frame to act on it.
pub fn requestQuit() void {
    if (!hasUnsavedChanges()) {
        quit_confirmed_flag = true;
        return;
    }
    pending = .quit;
}

/// True once a quit request has resolved (confirmed, or needed no
/// confirmation to begin with).
pub fn quitConfirmed() bool {
    return quit_confirmed_flag;
}

/// Request to open a different project in place of the current one. Opens
/// immediately if nothing is dirty; otherwise defers behind the
/// confirmation dialog.
pub fn requestSwitchProject(path: []const u8) void {
    if (!hasUnsavedChanges()) {
        ProjectOps.openProjectImmediate(path);
        return;
    }
    const n = @min(path.len, pending_path_buf.len);
    @memcpy(pending_path_buf[0..n], path[0..n]);
    pending_path_len = n;
    pending = .switch_project;
}

fn proceed() void {
    switch (pending) {
        .none => {},
        .quit => quit_confirmed_flag = true,
        .switch_project => ProjectOps.openProjectImmediate(pendingPath()),
    }
    pending = .none;
}

/// Modal save/discard/cancel prompt shown when a pending quit or project
/// switch would discard unsaved edits. Drawn every frame from `Window.frame`.
pub fn drawPendingDialog() void {
    if (pending == .none) return;

    var win = gui.floatingWindow(@src(), .{
        .modal = true,
        .center_on = gui.currentWindow().subwindows.current_rect,
        .window_avoid = .nudge,
    }, .{ .role = .dialog, .min_size_content = .{ .w = 320 } });
    defer win.deinit();

    var open_flag = true;
    win.dragAreaSet(gui.windowHeader(tr("Unsaved Changes"), "", &open_flag));
    if (!open_flag) {
        pending = .none;
        return;
    }

    const message = switch (pending) {
        .quit => tr("You have unsaved changes. Save before quitting?"),
        else => tr("You have unsaved changes. Save before switching projects?"),
    };
    gui.label(@src(), "{s}", .{message}, .{ .padding = .all(8) });

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_x = 1.0, .padding = .all(4) });
    defer row.deinit();

    if (gui.button(@src(), tr("Save All"), .{}, .{})) {
        Documents.saveAll();
        proceed();
    }
    if (gui.button(@src(), tr("Don't Save"), .{}, .{ .id_extra = 1 })) {
        proceed();
    }
    if (gui.button(@src(), tr("Cancel"), .{}, .{ .id_extra = 2 })) {
        pending = .none;
    }
}
