//! Read-side view over a `TaskManager` snapshot: separates roots from their
//! children, rolls child progress up into an aggregate for the parent, and
//! formats the one line a compact display can afford. Shared by the Studio
//! task bar and the CLI reporter so both describe a run the same way.
const std = @import("std");
const task_mod = @import("Task.zig");

const Task = task_mod.Task;
const Status = task_mod.Status;

/// Aggregate state of one root and everything beneath it.
pub const Rollup = struct {
    /// Completion in 0..1: the weighted mean of child progress when the task
    /// has children, otherwise the task's own fraction.
    progress: f32 = 0,
    /// Effective status — a parent reads as `running` while any child is, and
    /// inherits the worst terminal outcome of its children otherwise.
    status: Status = .queued,
    child_count: usize = 0,
    children_done: usize = 0,
    /// Deepest running child, whose label is the most specific thing to show.
    active_child: ?Task = null,
};

/// Roll `root` up over its children in `tasks`.
pub fn rollup(tasks: []const Task, root: Task) Rollup {
    var r = Rollup{ .progress = root.fraction(), .status = root.status };

    var weight_total: f32 = 0;
    var weighted: f32 = 0;
    var any_active = false;
    var any_failed = false;
    var any_cancelled = false;

    for (tasks) |*c| {
        if (c.parent_id != root.id) continue;
        r.child_count += 1;
        const w = if (c.weight > 0) c.weight else 1;
        weight_total += w;
        weighted += w * c.fraction();
        switch (c.status) {
            .completed => r.children_done += 1,
            .failed => {
                any_failed = true;
                r.children_done += 1;
            },
            .cancelled => {
                any_cancelled = true;
                r.children_done += 1;
            },
            .running => {
                any_active = true;
                if (r.active_child == null) r.active_child = c.*;
            },
            .queued, .blocked => any_active = true,
        }
    }

    if (r.child_count == 0) return r;

    // Divide by the planned total when the owner declared one, so the bar does
    // not lurch backwards each time another phase opens.
    const denom = @max(weight_total, root.planned_weight);
    r.progress = if (denom > 0) @min(weighted / denom, 1) else 0;
    // A finished root is authoritative: it knows outcomes children can't see.
    if (root.isFinished()) {
        r.progress = if (root.status == .completed) 1 else r.progress;
        return r;
    }
    r.status = if (any_active) .running else if (any_failed) .failed else if (any_cancelled) .cancelled else .completed;
    return r;
}

/// Copy the root tasks (those without a parent) into `buf`, preserving
/// submission order. Returns the slice actually written.
pub fn roots(tasks: []const Task, buf: []Task) []Task {
    var n: usize = 0;
    for (tasks) |*t| {
        if (t.parent_id != 0 or n == buf.len) continue;
        buf[n] = t.*;
        n += 1;
    }
    return buf[0..n];
}

/// Copy the direct children of `parent_id` into `buf`, preserving order.
pub fn childrenOf(tasks: []const Task, parent_id: u64, buf: []Task) []Task {
    var n: usize = 0;
    for (tasks) |*t| {
        if (t.parent_id != parent_id or n == buf.len) continue;
        buf[n] = t.*;
        n += 1;
    }
    return buf[0..n];
}

/// The active root a single-line display should summarise: the most disruptive
/// kind, and among equals the one submitted first (it will finish first).
pub fn primary(tasks: []const Task) ?Task {
    var best: ?Task = null;
    for (tasks) |*t| {
        if (t.parent_id != 0 or !t.isActive()) continue;
        const b = best orelse {
            best = t.*;
            continue;
        };
        if (t.kind.priority() > b.kind.priority()) best = t.*;
    }
    return best;
}

/// Number of active roots — how many things are running in parallel.
pub fn activeRoots(tasks: []const Task) usize {
    var n: usize = 0;
    for (tasks) |*t| {
        if (t.parent_id == 0 and t.isActive()) n += 1;
    }
    return n;
}

/// Longest line a `describe` result can occupy.
pub const DESCRIBE_CAP = task_mod.MAX_LABEL + task_mod.MAX_NOTE + 48;

/// One-line description of a root: its label plus the most specific detail
/// available — the running child's label, an item count, or the raw note.
/// Writes into `buf`; falls back to the bare label if formatting overflows.
pub fn describe(task: Task, roll: Rollup, buf: []u8) []const u8 {
    if (roll.active_child) |c| {
        const detail = if (c.units_total > 0)
            std.fmt.bufPrint(buf, "{s} — {s} ({d}/{d})", .{ task.label(), c.label(), c.units_done, c.units_total })
        else
            std.fmt.bufPrint(buf, "{s} — {s}", .{ task.label(), c.label() });
        return detail catch task.label();
    }
    if (task.units_total > 0) {
        return std.fmt.bufPrint(buf, "{s} ({d}/{d})", .{ task.label(), task.units_done, task.units_total }) catch task.label();
    }
    if (task.note().len > 0) {
        return std.fmt.bufPrint(buf, "{s}: {s}", .{ task.label(), task.note() }) catch task.label();
    }
    return task.label();
}

// ── Tests ─────────────────────────────────────────────────────────────────────

fn mk(id: u64, parent: u64, status: Status, progress: f32, weight: f32, label: []const u8) Task {
    var t = Task{ .id = id, .parent_id = parent, .status = status, .progress = progress, .weight = weight };
    @memcpy(t.label_buf[0..label.len], label);
    t.label_len = label.len;
    return t;
}

test "a childless root rolls up to its own fraction" {
    const tasks = [_]Task{mk(1, 0, .running, 0.4, 1, "Import assets")};
    const r = rollup(&tasks, tasks[0]);
    try std.testing.expectApproxEqAbs(@as(f32, 0.4), r.progress, 0.0001);
    try std.testing.expectEqual(@as(usize, 0), r.child_count);
    try std.testing.expectEqual(Status.running, r.status);
}

test "aggregate progress is the weighted mean of children" {
    const tasks = [_]Task{
        mk(1, 0, .running, 0, 1, "Build game"),
        mk(2, 1, .completed, 1, 1, "Generate project"),
        mk(3, 1, .running, 0.5, 3, "Compile game"),
    };
    const r = rollup(&tasks, tasks[0]);
    // (1*1 + 3*0.5) / 4
    try std.testing.expectApproxEqAbs(@as(f32, 0.625), r.progress, 0.0001);
    try std.testing.expectEqual(@as(usize, 2), r.child_count);
    try std.testing.expectEqual(@as(usize, 1), r.children_done);
    try std.testing.expectEqualStrings("Compile game", r.active_child.?.label());
}

test "equal weights reduce to completed-over-total" {
    const tasks = [_]Task{
        mk(1, 0, .running, 0, 1, "Importing textures"),
        mk(2, 1, .completed, 1, 1, "a"),
        mk(3, 1, .completed, 1, 1, "b"),
        mk(4, 1, .queued, 0, 1, "c"),
        mk(5, 1, .queued, 0, 1, "d"),
    };
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), rollup(&tasks, tasks[0]).progress, 0.0001);
}

test "a planned total keeps the aggregate from walking backwards" {
    var root = mk(1, 0, .running, 0, 1, "Build game");
    root.planned_weight = 16;

    // Only the light first phase has opened; it is 1/16 of the plan, not all
    // of the work seen so far.
    const early = [_]Task{ root, mk(2, 1, .completed, 1, 1, "Generate project") };
    try std.testing.expectApproxEqAbs(@as(f32, 0.0625), rollup(&early, root).progress, 0.0001);

    // Opening the heavy phase must not drag the parent back down.
    const later = [_]Task{
        root,
        mk(2, 1, .completed, 1, 1, "Generate project"),
        mk(3, 1, .running, 0, 15, "Compile game"),
    };
    try std.testing.expectApproxEqAbs(@as(f32, 0.0625), rollup(&later, root).progress, 0.0001);

    // Children heavier than planned still cap at 1 rather than overshooting.
    const over = [_]Task{ root, mk(2, 1, .completed, 1, 40, "Generate project") };
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), rollup(&over, root).progress, 0.0001);
}

test "a failed child fails the parent once nothing is running" {
    var tasks = [_]Task{
        mk(1, 0, .running, 0, 1, "Build game"),
        mk(2, 1, .completed, 1, 1, "Generate project"),
        mk(3, 1, .failed, 0.3, 1, "Compile game"),
    };
    try std.testing.expectEqual(Status.failed, rollup(&tasks, tasks[0]).status);
    // While a sibling is still running the parent stays running.
    tasks[1].status = .running;
    try std.testing.expectEqual(Status.running, rollup(&tasks, tasks[0]).status);
}

test "a finished root overrides its children's rollup" {
    const tasks = [_]Task{
        mk(1, 0, .completed, 1, 1, "Build game"),
        mk(2, 1, .cancelled, 0.2, 1, "Compile game"),
    };
    const r = rollup(&tasks, tasks[0]);
    try std.testing.expectEqual(Status.completed, r.status);
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), r.progress, 0.0001);
}

test "roots and children are separated in order" {
    const tasks = [_]Task{
        mk(1, 0, .running, 0, 1, "Build game"),
        mk(2, 1, .running, 0, 1, "Compile game"),
        mk(3, 0, .running, 0, 1, "Import assets"),
        mk(4, 1, .queued, 0, 1, "Copy output"),
    };
    var buf: [8]Task = undefined;
    const rs = roots(&tasks, &buf);
    try std.testing.expectEqual(@as(usize, 2), rs.len);
    try std.testing.expectEqualStrings("Build game", rs[0].label());
    try std.testing.expectEqualStrings("Import assets", rs[1].label());

    var cbuf: [8]Task = undefined;
    const cs = childrenOf(&tasks, 1, &cbuf);
    try std.testing.expectEqual(@as(usize, 2), cs.len);
    try std.testing.expectEqualStrings("Compile game", cs[0].label());
    try std.testing.expectEqualStrings("Copy output", cs[1].label());
}

test "primary picks the most disruptive active root" {
    var tasks = [_]Task{
        mk(1, 0, .running, 0, 1, "Import assets"),
        mk(2, 0, .running, 0, 1, "Build game"),
        mk(3, 1, .running, 0, 1, "child of import"),
    };
    tasks[0].kind = .import;
    tasks[1].kind = .build;
    tasks[2].kind = .build;
    try std.testing.expectEqualStrings("Build game", primary(&tasks).?.label());
    try std.testing.expectEqual(@as(usize, 2), activeRoots(&tasks));

    // Only finished roots left: nothing to summarise.
    tasks[0].status = .completed;
    tasks[1].status = .completed;
    try std.testing.expect(primary(&tasks) == null);
    try std.testing.expectEqual(@as(usize, 0), activeRoots(&tasks));
}

test "describe prefers the running child, then units, then the note" {
    var buf: [DESCRIBE_CAP]u8 = undefined;
    var root = mk(1, 0, .running, 0.5, 1, "Import assets");

    var child = mk(2, 1, .running, 0.5, 1, "Textures");
    child.units_done = 3;
    child.units_total = 9;
    const with_child = [_]Task{ root, child };
    try std.testing.expectEqualStrings(
        "Import assets — Textures (3/9)",
        describe(root, rollup(&with_child, root), &buf),
    );

    root.units_done = 12;
    root.units_total = 40;
    const alone = [_]Task{root};
    try std.testing.expectEqualStrings("Import assets (12/40)", describe(root, rollup(&alone, root), &buf));

    root.units_total = 0;
    @memcpy(root.note_buf[0..5], "brick");
    root.note_len = 5;
    const noted = [_]Task{root};
    try std.testing.expectEqualStrings("Import assets: brick", describe(root, rollup(&noted, root), &buf));
}
