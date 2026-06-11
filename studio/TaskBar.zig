//! Unity-style bottom task bar. Shows the current background task with an inline
//! progress bar and a cancel button, an expandable list of all tracked tasks,
//! and raises a toast when a task finishes.
//!
//! All state is read from the studio `TaskManager` (via `Tasks`) as a per-frame
//! snapshot, so this file never touches worker-thread state directly.
const std = @import("std");
const dvui = @import("dvui");
const editor = @import("editor");
const Tasks = @import("Tasks.zig");

const Task = editor.Task;

/// Whether the full task list is expanded above the status row.
var show_list = false;
/// Highest task id we have already raised a completion toast for.
var notified_id: u64 = 0;

/// Draw the bottom task bar. Call once per frame at the bottom of the layout.
pub fn draw() void {
    const tm = Tasks.tm();

    var buf: [editor.TaskManager.MAX_TASKS]Task = undefined;
    const tasks = buf[0..tm.snapshot(&buf)];

    notifyCompletions(tasks);

    var outer = dvui.box(@src(), .{ .dir = .vertical }, .{
        .expand = .horizontal,
        .background = true,
        .style = .control,
    });
    defer outer.deinit();

    if (show_list and tasks.len > 0) {
        var scroll = dvui.scrollArea(@src(), .{ .vertical = .auto }, .{
            .expand = .horizontal,
            .min_size_content = .{ .h = 110 },
        });
        defer scroll.deinit();
        for (tasks, 0..) |*t, i| drawRow(tm, t, i);
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal });
    drawStatusRow(tm, tasks);
}

/// The compact always-visible row: current task summary + expand toggle.
fn drawStatusRow(tm: *editor.TaskManager, tasks: []Task) void {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .padding = dvui.Rect.all(4),
    });
    defer row.deinit();

    // The most recently started active task drives the inline summary.
    var active: ?*Task = null;
    var active_count: usize = 0;
    for (tasks) |*t| {
        if (t.isActive()) {
            active = t;
            active_count += 1;
        }
    }

    if (active) |t| {
        var line_buf: [editor.TaskManager.MAX_LABEL + editor.TaskManager.MAX_NOTE + 8]u8 = undefined;
        const line = if (t.note().len > 0)
            std.fmt.bufPrint(&line_buf, "{s}: {s}", .{ t.label(), t.note() }) catch t.label()
        else
            t.label();
        dvui.label(@src(), "{s}", .{line}, .{ .gravity_y = 0.5 });
        dvui.progress(@src(), .{ .percent = t.progress }, .{
            .min_size_content = .{ .w = 160, .h = 12 },
            .gravity_y = 0.5,
            .margin = dvui.Rect.all(6),
        });
        if (dvui.button(@src(), if (t.cancel_requested) "Cancelling..." else "Cancel", .{}, .{ .gravity_y = 0.5 })) {
            tm.requestCancel(t.id);
        }
    } else {
        dvui.label(@src(), "Ready", .{}, .{ .gravity_y = 0.5 });
    }

    _ = dvui.spacer(@src(), .{ .expand = .horizontal });

    if (tasks.len > 0) {
        if (dvui.button(@src(), "Clear", .{}, .{ .gravity_y = 0.5 })) tm.clearFinished();
    }

    var btn_buf: [32]u8 = undefined;
    const btn = std.fmt.bufPrint(&btn_buf, "Tasks ({d})", .{active_count}) catch "Tasks";
    if (dvui.button(@src(), btn, .{}, .{ .gravity_y = 0.5 })) show_list = !show_list;
}

/// One row in the expanded list: kind, label, progress/status, cancel.
fn drawRow(tm: *editor.TaskManager, t: *const Task, i: usize) void {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .id_extra = i,
        .padding = dvui.Rect.all(3),
    });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{t.kind.text()}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 70 },
    });
    dvui.label(@src(), "{s}", .{t.label()}, .{ .gravity_y = 0.5 });

    _ = dvui.spacer(@src(), .{ .expand = .horizontal });

    if (t.isActive()) {
        dvui.progress(@src(), .{ .percent = t.progress }, .{
            .min_size_content = .{ .w = 140, .h = 12 },
            .gravity_y = 0.5,
            .margin = dvui.Rect.all(4),
        });
        if (dvui.button(@src(), if (t.cancel_requested) "Cancelling..." else "Cancel", .{}, .{ .gravity_y = 0.5 })) {
            tm.requestCancel(t.id);
        }
    } else {
        dvui.label(@src(), "{s}", .{t.status.text()}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 90 },
        });
    }
}

/// Raise a toast for any task that has newly reached a terminal state.
fn notifyCompletions(tasks: []Task) void {
    var max_seen = notified_id;
    for (tasks) |*t| {
        if (!t.isFinished() or t.id <= notified_id) continue;
        if (t.id > max_seen) max_seen = t.id;

        var msg_buf: [editor.TaskManager.MAX_LABEL + 48]u8 = undefined;
        const msg = switch (t.status) {
            .completed => std.fmt.bufPrint(&msg_buf, "{s}: done", .{t.label()}) catch t.label(),
            .failed => std.fmt.bufPrint(&msg_buf, "{s}: failed", .{t.label()}) catch t.label(),
            .cancelled => std.fmt.bufPrint(&msg_buf, "{s}: cancelled", .{t.label()}) catch t.label(),
            else => unreachable,
        };
        dvui.toast(@src(), .{ .id_extra = @intCast(t.id), .message = msg });
    }
    notified_id = max_seen;
}
