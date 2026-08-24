//! Plain-text rendering of tasks for CLI and headless callers, so a terminal
//! run of a build or import reports the same structure the Studio task bar
//! shows: one aggregate line per root, indented lines for nested phases.
const std = @import("std");
const TaskManager = @import("TaskManager.zig");
const task_tree = @import("TaskTree.zig");
const Progress = @import("Progress.zig").Progress;
const task_mod = @import("Task.zig");

const Task = task_mod.Task;
const Kind = task_mod.Kind;

/// Widest line `format` can produce.
pub const LINE_CAP = task_tree.DESCRIBE_CAP + 32;

/// Where rendered lines go. Swapped out in tests; defaults to stderr.
pub const Sink = *const fn (line: []const u8) void;

fn stderrSink(line: []const u8) void {
    std.debug.print("{s}\n", .{line});
}

/// Render one root as `[ 42%] Build game — Compile game (12.3s)`. Elapsed is
/// omitted until `TaskManager.tick` has stamped the task.
pub fn format(tasks: []const Task, root: Task, now_ms: i64, buf: []u8) []const u8 {
    const roll = task_tree.rollup(tasks, root);
    var detail_buf: [task_tree.DESCRIBE_CAP]u8 = undefined;
    const detail = task_tree.describe(&root, roll, &detail_buf);
    const pct: u32 = @intFromFloat(@round(std.math.clamp(roll.progress, 0, 1) * 100));

    const elapsed = root.elapsedMs(now_ms);
    if (elapsed > 0) {
        const secs = @as(f64, @floatFromInt(elapsed)) / 1000.0;
        return std.fmt.bufPrint(buf, "[{d:>3}%] {s} ({d:.1}s)", .{ pct, detail, secs }) catch detail;
    }
    return std.fmt.bufPrint(buf, "[{d:>3}%] {s}", .{ pct, detail }) catch detail;
}

/// Write every root, and each root's children indented beneath it, to `sink`.
/// Used for a final summary and for `--tasks`-style listings.
pub fn dump(tasks: []const Task, now_ms: i64, sink: Sink) void {
    var line: [LINE_CAP]u8 = undefined;
    for (tasks) |*t| {
        if (t.parent_id != 0) continue;
        sink(format(tasks, t.*, now_ms, &line));
        for (tasks) |*c| {
            if (c.parent_id != t.id) continue;
            const pct: u32 = @intFromFloat(@round(std.math.clamp(c.fraction(), 0, 1) * 100));
            const child_line = std.fmt.bufPrint(&line, "        [{d:>3}%] {s} — {s}", .{
                pct,
                c.label(),
                c.status.text(),
            }) catch c.label();
            sink(child_line);
        }
    }
}

/// A running task rendered to a text sink. Wraps a `TaskManager` so nested
/// phases and item counters recorded by an operation are reported identically
/// in the terminal and in the editor.
pub const Reporter = struct {
    tm: *TaskManager,
    id: u64,
    sink: Sink = stderrSink,
    /// Last line emitted, so an operation reporting 60 times a second does not
    /// flood the terminal with identical text.
    last_buf: [LINE_CAP]u8 = undefined,
    last_len: usize = 0,

    /// Begin a root task and return a reporter bound to it.
    pub fn start(tm: *TaskManager, kind: Kind, label: []const u8) Reporter {
        return .{ .tm = tm, .id = tm.begin(kind, label) };
    }

    pub fn progress(self: *Reporter) Progress {
        return .{ .ctx = self, .id = self.id, .vtable = &vtable };
    }

    /// Move the task to its terminal state, preferring a cancel observed
    /// mid-flight, then print the final line.
    pub fn finish(self: *Reporter, ok: bool, fail_message: []const u8) void {
        if (self.tm.isCancelRequested(self.id)) {
            self.tm.cancel(self.id);
        } else if (ok) {
            self.tm.complete(self.id);
        } else {
            self.tm.fail(self.id, fail_message);
        }
        self.emit();
        if (self.tm.get(self.id)) |t| {
            var buf: [LINE_CAP]u8 = undefined;
            const line = std.fmt.bufPrint(&buf, "{s}: {s}", .{ t.kind.text(), t.status.text() }) catch return;
            self.sink(line);
        }
    }

    /// Render the current state, suppressing repeats of the previous line.
    fn emit(self: *Reporter) void {
        var snap: [TaskManager.MAX_TASKS]Task = undefined;
        const tasks = snap[0..self.tm.snapshot(&snap)];
        const root = for (tasks) |*t| {
            if (t.id == self.id) break t.*;
        } else return;

        var buf: [LINE_CAP]u8 = undefined;
        const line = format(tasks, root, 0, &buf);
        if (std.mem.eql(u8, line, self.last_buf[0..self.last_len])) return;
        const n = @min(line.len, self.last_buf.len);
        @memcpy(self.last_buf[0..n], line[0..n]);
        self.last_len = n;
        self.sink(line);
    }

    // ── Progress vtable ───────────────────────────────────────────────────────

    fn report(ctx: ?*anyopaque, id: u64, fraction: f32, note: []const u8) void {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        self.tm.setProgress(id, fraction, note);
        self.emit();
    }

    fn cancelled(ctx: ?*anyopaque, id: u64) bool {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        return self.tm.isCancelRequested(id);
    }

    fn beginChild(ctx: ?*anyopaque, parent_id: u64, kind: Kind, label: []const u8, weight: f32) u64 {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        const child = self.tm.beginChild(parent_id, kind, label, weight);
        self.emit();
        return child;
    }

    fn endChild(ctx: ?*anyopaque, id: u64, ok: bool) void {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        if (ok) self.tm.complete(id) else self.tm.fail(id, "");
        self.emit();
    }

    fn units(ctx: ?*anyopaque, id: u64, done: u64, total: u64) void {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        self.tm.setUnits(id, done, total);
        self.emit();
    }

    fn planChildren(ctx: ?*anyopaque, id: u64, total_weight: f32) void {
        const self: *Reporter = @ptrCast(@alignCast(ctx.?));
        self.tm.setPlannedWeight(id, total_weight);
    }

    const vtable = Progress.VTable{
        .report = report,
        .cancelled = cancelled,
        .beginChild = beginChild,
        .endChild = endChild,
        .units = units,
        .planChildren = planChildren,
    };
};

// ── Tests ─────────────────────────────────────────────────────────────────────

var test_lines: [32][LINE_CAP]u8 = undefined;
var test_lens: [32]usize = @splat(0);
var test_count: usize = 0;

fn testSink(line: []const u8) void {
    if (test_count == test_lines.len) return;
    const n = @min(line.len, LINE_CAP);
    @memcpy(test_lines[test_count][0..n], line[0..n]);
    test_lens[test_count] = n;
    test_count += 1;
}

fn testLine(i: usize) []const u8 {
    return test_lines[i][0..test_lens[i]];
}

test "format renders aggregate percent and the running child" {
    var tm = TaskManager.init();
    const root = tm.begin(.build, "Build game");
    const phase = tm.beginChild(root, .compile, "Compile game", 1);
    tm.setProgress(phase, 0.5, "");

    var snap: [TaskManager.MAX_TASKS]Task = undefined;
    const tasks = snap[0..tm.snapshot(&snap)];
    var buf: [LINE_CAP]u8 = undefined;
    try std.testing.expectEqualStrings(
        "[ 50%] Build game — Compile game",
        format(tasks, tasks[0], 0, &buf),
    );
}

test "format appends elapsed once tick has stamped the task" {
    var tm = TaskManager.init();
    const root = tm.begin(.import, "Import assets");
    tm.setUnits(root, 1, 4);
    tm.tick(1_000);

    var snap: [TaskManager.MAX_TASKS]Task = undefined;
    const tasks = snap[0..tm.snapshot(&snap)];
    var buf: [LINE_CAP]u8 = undefined;
    try std.testing.expectEqualStrings(
        "[ 25%] Import assets (1/4) (2.5s)",
        format(tasks, tasks[0], 3_500, &buf),
    );
}

test "dump lists roots with indented children" {
    test_count = 0;
    var tm = TaskManager.init();
    const root = tm.begin(.build, "Build game");
    const gen = tm.beginChild(root, .generic, "Generate project", 1);
    tm.complete(gen);
    _ = tm.beginChild(root, .compile, "Compile game", 3);

    var snap: [TaskManager.MAX_TASKS]Task = undefined;
    const tasks = snap[0..tm.snapshot(&snap)];
    dump(tasks, 0, testSink);

    try std.testing.expectEqual(@as(usize, 3), test_count);
    try std.testing.expectEqualStrings("[ 25%] Build game — Compile game", testLine(0));
    try std.testing.expectEqualStrings("        [100%] Generate project — Completed", testLine(1));
    try std.testing.expectEqualStrings("        [  0%] Compile game — Running", testLine(2));
}

test "reporter suppresses repeated lines and prints a final status" {
    test_count = 0;
    var tm = TaskManager.init();
    var rep = Reporter.start(&tm, .import, "Import assets");
    rep.sink = testSink;

    const p = rep.progress();
    p.units(1, 4);
    p.units(1, 4); // identical line: suppressed
    p.units(2, 4);
    try std.testing.expectEqual(@as(usize, 2), test_count);
    try std.testing.expectEqualStrings("[ 25%] Import assets (1/4)", testLine(0));
    try std.testing.expectEqualStrings("[ 50%] Import assets (2/4)", testLine(1));

    rep.finish(true, "");
    try std.testing.expectEqualStrings("[100%] Import assets (4/4)", testLine(2));
    try std.testing.expectEqualStrings("Import: Completed", testLine(3));
}

test "reporter finish honours an observed cancellation" {
    test_count = 0;
    var tm = TaskManager.init();
    var rep = Reporter.start(&tm, .build, "Build game");
    rep.sink = testSink;

    tm.requestCancel(rep.id);
    try std.testing.expect(rep.progress().cancelled());
    rep.finish(true, "");
    try std.testing.expectEqual(TaskManager.Status.cancelled, tm.get(rep.id).?.status);
}
