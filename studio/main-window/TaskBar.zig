//! Unity-style bottom task bar. The always-visible row summarises the single
//! most disruptive background operation — one aggregate bar even when it is
//! made of many nested phases — and an expandable list below it exposes every
//! task, including children, for anyone who wants the detail.
//!
//! All state is read from the studio `TaskManager` (via `Tasks`) as a per-frame
//! snapshot, so this file never touches worker-thread state directly.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const Tasks = @import("Tasks.zig");
const TaskList = @import("TaskList.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const Task = editor.Task;
const task_tree = editor.task_tree;

/// Whether the full task list is expanded above the status row.
var show_list = false;
/// Ids already toasted, as a ring. A monotonic high-water mark would swallow
/// the completion of an older task that finished after a newer one — routine
/// while tasks run in parallel.
var notified: [editor.TaskManager.MAX_TASKS]u64 = @splat(0);
var notified_next: usize = 0;

/// Draw the bottom task bar. Call once per frame at the bottom of the layout.
pub fn draw() void {
    const tm = Tasks.tm();
    const now_ms = Tasks.nowMs();

    var buf: [editor.TaskManager.MAX_TASKS]Task = undefined;
    const tasks = buf[0..tm.snapshot(&buf)];

    notifyCompletions(tasks);

    var outer = gui.box(@src(), .{ .dir = .vertical }, .{
        .expand = .horizontal,
        .background = true,
        .style = .control,
    });
    defer outer.deinit();

    if (show_list and tasks.len > 0) TaskList.draw(tm, tasks, now_ms);

    _ = gui.separator(@src(), .{ .expand = .horizontal });
    drawStatusRow(tm, tasks, now_ms);
}

/// The compact always-visible row: one aggregate summary of the most
/// disruptive active task, how many others run alongside it, and the toggle.
fn drawStatusRow(tm: *editor.TaskManager, tasks: []Task, now_ms: i64) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .padding = gui.Rect.all(4),
    });
    defer row.deinit();

    const active_roots = task_tree.activeRoots(tasks);

    if (task_tree.primary(tasks)) |t| {
        const roll = task_tree.rollup(tasks, t);
        var line_buf: [task_tree.DESCRIBE_CAP]u8 = undefined;
        gui.label(@src(), "{s}", .{task_tree.describe(&t, roll, &line_buf)}, .{ .gravity_y = 0.5 });

        // Backgrounded so the *unfilled* part of the track reads as a bar too;
        // without it a task at 5% looks like a stray dot on the status row.
        gui.progress(@src(), .{ .percent = roll.progress }, .{
            .min_size_content = .{ .w = 160, .h = 12 },
            .gravity_y = 0.5,
            .margin = gui.Rect.all(6),
            .background = true,
            .style = .content,
        });

        // Elapsed matters most on the operations that hurt — a script compile
        // on Windows runs for minutes with little else to show for itself.
        var elapsed_buf: [16]u8 = undefined;
        const elapsed = t.elapsedMs(now_ms);
        if (elapsed >= 1_000) {
            gui.label(@src(), "{s}", .{TaskList.formatElapsed(elapsed, &elapsed_buf)}, .{ .gravity_y = 0.5 });
        }

        if (gui.button(@src(), if (t.cancel_requested) tr("Cancelling...") else tr("Cancel"), .{}, .{ .gravity_y = 0.5 })) {
            tm.requestCancel(t.id);
        }

        // Parallel work is real but must not steal the row; a count is enough
        // to tell the user the list holds more than what they see here.
        if (active_roots > 1) {
            const more = StudioLocale.trArgs(
                "+{count} more",
                &.{.{ .name = "count", .value = .{ .number = @intCast(active_roots - 1) } }},
            );
            gui.label(@src(), "{s}", .{more}, .{ .gravity_y = 0.5 });
        }
    } else {
        gui.label(@src(), "{s}", .{tr("Ready")}, .{ .gravity_y = 0.5 });
    }

    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    const btn = StudioLocale.trArgs("Tasks ({count})", &.{.{ .name = "count", .value = .{ .number = @intCast(active_roots) } }});
    if (gui.button(@src(), btn, .{}, .{ .gravity_y = 0.5 })) show_list = !show_list;
}

/// Raise a toast for any root task that has newly reached a terminal state.
/// Children are skipped — a build would otherwise toast once per phase.
fn notifyCompletions(tasks: []Task) void {
    for (tasks) |*t| {
        if (t.parent_id != 0 or !t.isFinished() or wasNotified(t.id)) continue;
        markNotified(t.id);

        const msg = switch (t.status) {
            .completed => StudioLocale.trArgs("{label}: done", &.{.{ .name = "label", .value = .{ .text = t.label() } }}),
            .failed => StudioLocale.trArgs("{label}: failed", &.{.{ .name = "label", .value = .{ .text = t.label() } }}),
            .cancelled => StudioLocale.trArgs("{label}: cancelled", &.{.{ .name = "label", .value = .{ .text = t.label() } }}),
            else => unreachable,
        };
        gui.toast(@src(), .{ .id_extra = @intCast(t.id), .message = msg });
    }
}

fn wasNotified(id: u64) bool {
    for (notified) |n| {
        if (n == id) return true;
    }
    return false;
}

fn markNotified(id: u64) void {
    notified[notified_next] = id;
    notified_next = (notified_next + 1) % notified.len;
}
