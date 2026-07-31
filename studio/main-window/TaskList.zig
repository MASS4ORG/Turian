//! The expanded half of the task bar: one row per root task showing its
//! aggregate progress, with nested phases hidden behind an expander so a batch
//! operation reads as a single line until someone asks for the breakdown.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const Task = editor.Task;
const task_tree = editor.task_tree;

/// Render the scrollable list above the status row.
pub fn draw(tm: *editor.TaskManager, tasks: []Task, now_ms: i64) void {
    drawHeader(tm, tasks);

    var scroll = gui.scrollArea(@src(), .{ .vertical = .auto }, .{
        .expand = .horizontal,
        .min_size_content = .{ .h = 110 },
    });
    defer scroll.deinit();

    var root_buf: [editor.TaskManager.MAX_TASKS]Task = undefined;
    for (task_tree.roots(tasks, &root_buf), 0..) |*root, i| {
        drawRoot(tm, tasks, root, i, now_ms);
    }
}

/// Counts plus the manual cleanup escape hatch. Completed tasks retire on
/// their own; failures stay until read, and this is how they are dismissed.
fn drawHeader(tm: *editor.TaskManager, tasks: []Task) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .padding = gui.Rect.all(3),
    });
    defer row.deinit();

    var finished: usize = 0;
    for (tasks) |*t| {
        if (t.parent_id == 0 and t.isFinished()) finished += 1;
    }

    const summary = StudioLocale.trArgs("{active} running, {done} finished", &.{
        .{ .name = "active", .value = .{ .number = @intCast(task_tree.activeRoots(tasks)) } },
        .{ .name = "done", .value = .{ .number = @intCast(finished) } },
    });
    gui.label(@src(), "{s}", .{summary}, .{ .gravity_y = 0.5 });

    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    if (finished > 0) {
        if (gui.button(@src(), tr("Clear finished"), .{}, .{ .gravity_y = 0.5 })) tm.clearFinished();
    }
}

/// One root: aggregate bar, elapsed, cancel — and its phases when expanded.
fn drawRoot(tm: *editor.TaskManager, tasks: []Task, root: *const Task, i: usize, now_ms: i64) void {
    const roll = task_tree.rollup(tasks, root.*);

    var outer = gui.box(@src(), .{ .dir = .vertical }, .{
        .expand = .horizontal,
        .id_extra = i,
    });
    defer outer.deinit();

    var expanded = false;
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = gui.Rect.all(3),
        });
        defer row.deinit();

        if (roll.child_count > 0) {
            var head_buf: [task_tree.DESCRIBE_CAP]u8 = undefined;
            const head = std.fmt.bufPrint(&head_buf, "{s} ({d}/{d})", .{
                root.label(),
                roll.children_done,
                roll.child_count,
            }) catch root.label();
            expanded = gui.expander(@src(), head, .{ .default_expanded = false }, .{
                .gravity_y = 0.5,
                .min_size_content = .{ .w = 220 },
            });
        } else {
            gui.label(@src(), "{s}", .{root.kind.text()}, .{
                .gravity_y = 0.5,
                .min_size_content = .{ .w = 70 },
            });
            var label_buf: [LABEL_CAP]u8 = undefined;
            gui.label(@src(), "{s}", .{labelWithUnits(root, &label_buf)}, .{ .gravity_y = 0.5 });
        }

        _ = gui.spacer(@src(), .{ .expand = .horizontal });

        var elapsed_buf: [16]u8 = undefined;
        const elapsed = root.elapsedMs(now_ms);
        if (elapsed >= 1_000) {
            gui.label(@src(), "{s}", .{formatElapsed(elapsed, &elapsed_buf)}, .{ .gravity_y = 0.5 });
        }

        if (root.isActive()) {
            gui.progress(@src(), .{ .percent = roll.progress }, .{
                .min_size_content = .{ .w = 140, .h = 12 },
                .gravity_y = 0.5,
                .margin = gui.Rect.all(4),
                .background = true,
                .style = .content,
            });
            const label = if (root.cancel_requested)
                tr("Cancelling...")
            else if (root.status == .blocked or root.status == .queued)
                tr("Skip")
            else
                tr("Cancel");
            if (gui.button(@src(), label, .{}, .{ .gravity_y = 0.5 })) tm.requestCancel(root.id);
        } else {
            gui.label(@src(), "{s}", .{root.status.text()}, .{
                .gravity_y = 0.5,
                .min_size_content = .{ .w = 90 },
            });
        }
    }

    // A failure is the one note worth surfacing without expanding anything.
    if (root.status == .failed and root.note().len > 0) {
        gui.label(@src(), "{s}", .{root.note()}, .{
            .margin = .{ .x = 24 },
            .style = .err,
        });
    }

    if (!expanded) return;
    var child_buf: [editor.TaskManager.MAX_TASKS]Task = undefined;
    for (task_tree.childrenOf(tasks, root.id, &child_buf), 0..) |*child, j| {
        drawChild(tm, child, j, now_ms);
    }
}

/// One nested phase, indented under its parent.
fn drawChild(tm: *editor.TaskManager, child: *const Task, j: usize, now_ms: i64) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .id_extra = j,
        .margin = .{ .x = 24 },
        .padding = gui.Rect.all(2),
    });
    defer row.deinit();

    var line_buf: [LABEL_CAP]u8 = undefined;
    gui.label(@src(), "{s}", .{labelWithUnits(child, &line_buf)}, .{ .gravity_y = 0.5 });

    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    var elapsed_buf: [16]u8 = undefined;
    const elapsed = child.elapsedMs(now_ms);
    if (elapsed >= 1_000) {
        gui.label(@src(), "{s}", .{formatElapsed(elapsed, &elapsed_buf)}, .{ .gravity_y = 0.5 });
    }

    if (child.isActive()) {
        gui.progress(@src(), .{ .percent = child.fraction() }, .{
            .min_size_content = .{ .w = 110, .h = 10 },
            .gravity_y = 0.5,
            .margin = gui.Rect.all(4),
            .background = true,
            .style = .content,
        });
        if (gui.button(@src(), tr("Cancel"), .{}, .{ .gravity_y = 0.5 })) tm.requestCancel(child.id);
    } else {
        gui.label(@src(), "{s}", .{child.status.text()}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 90 },
        });
    }
}

const LABEL_CAP = editor.TaskManager.MAX_LABEL + 32;

/// A task's label, suffixed with its item counters when it tracks them —
/// "142 / 1035" says far more about a long import than a bar position does.
fn labelWithUnits(task: *const Task, buf: []u8) []const u8 {
    if (task.units_total == 0) return task.label();
    return std.fmt.bufPrint(buf, "{s} ({d}/{d})", .{
        task.label(),
        task.units_done,
        task.units_total,
    }) catch task.label();
}

/// Render a duration as `12s` or `4:07`, the two forms that fit a status row.
pub fn formatElapsed(ms: i64, buf: []u8) []const u8 {
    const secs = @divTrunc(ms, 1000);
    if (secs < 60) return std.fmt.bufPrint(buf, "{d}s", .{secs}) catch "";
    return std.fmt.bufPrint(buf, "{d}:{d:0>2}", .{ @divTrunc(secs, 60), @mod(secs, 60) }) catch "";
}

test "elapsed switches to minutes past a minute" {
    var buf: [16]u8 = undefined;
    try std.testing.expectEqualStrings("0s", formatElapsed(400, &buf));
    try std.testing.expectEqualStrings("12s", formatElapsed(12_400, &buf));
    try std.testing.expectEqualStrings("1:00", formatElapsed(60_000, &buf));
    try std.testing.expectEqualStrings("4:07", formatElapsed(247_000, &buf));
}
