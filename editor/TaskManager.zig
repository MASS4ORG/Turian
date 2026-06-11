//! Editor task manager — a thread-safe registry of long-running operations
//! (asset import, script compilation, game build) with progress reporting and
//! cooperative cancellation.
//!
//! The studio runs heavy operations on worker threads that update task state
//! while the UI thread reads snapshots each frame; a single mutex guards all
//! access. Headless callers (the CLI) use the same API synchronously.
//!
//! Tasks are stored in a fixed array (no allocation). Each task carries a
//! monotonically increasing id, a kind, a status, a 0..1 progress value, a
//! short label and note, and a cancel-requested flag that operations poll via
//! the `Progress` interface returned by `progressFor`.
const std = @import("std");
const Progress = @import("Progress.zig").Progress;

/// Maximum number of tasks tracked at once. Oldest finished tasks are reclaimed
/// when the registry is full and a new task is created.
pub const MAX_TASKS = 64;
pub const MAX_LABEL = 96;
pub const MAX_NOTE = 128;

pub const Status = enum {
    queued,
    running,
    completed,
    failed,
    cancelled,

    pub fn text(self: Status) []const u8 {
        return switch (self) {
            .queued => "Queued",
            .running => "Running",
            .completed => "Completed",
            .failed => "Failed",
            .cancelled => "Cancelled",
        };
    }
};

pub const Kind = enum {
    generic,
    import,
    compile,
    build,

    pub fn text(self: Kind) []const u8 {
        return switch (self) {
            .generic => "Task",
            .import => "Import",
            .compile => "Compile",
            .build => "Build",
        };
    }
};

pub const Task = struct {
    id: u64 = 0,
    kind: Kind = .generic,
    status: Status = .queued,
    /// Completion fraction in the range 0..1.
    progress: f32 = 0,
    cancel_requested: bool = false,
    label_buf: [MAX_LABEL]u8 = undefined,
    label_len: usize = 0,
    note_buf: [MAX_NOTE]u8 = undefined,
    note_len: usize = 0,

    pub fn label(self: *const Task) []const u8 {
        return self.label_buf[0..self.label_len];
    }

    pub fn note(self: *const Task) []const u8 {
        return self.note_buf[0..self.note_len];
    }

    /// True once the task has reached a terminal state.
    pub fn isFinished(self: *const Task) bool {
        return switch (self.status) {
            .completed, .failed, .cancelled => true,
            .queued, .running => false,
        };
    }

    pub fn isActive(self: *const Task) bool {
        return !self.isFinished();
    }
};

/// Tiny atomic spinlock. Critical sections are bounded array scans of at most
/// `MAX_TASKS`, and contention is negligible (the UI reads once per frame while
/// a worker reports a few times per second), so a spinlock avoids threading an
/// `Io` through every method the way `std.Io.Mutex` would require.
const Guard = struct {
    held: std.atomic.Value(bool) = .init(false),

    fn lock(self: *Guard) void {
        while (self.held.swap(true, .acquire)) std.atomic.spinLoopHint();
    }
    fn unlock(self: *Guard) void {
        self.held.store(false, .release);
    }
};

guard: Guard = .{},
tasks: [MAX_TASKS]Task = undefined,
count: usize = 0,
next_id: u64 = 1,

const TaskManager = @This();

pub fn init() TaskManager {
    return .{};
}

// ── Internal helpers (caller must hold the mutex) ─────────────────────────────

fn setBuf(buf: []u8, len: *usize, text: []const u8) void {
    const n = @min(text.len, buf.len);
    @memcpy(buf[0..n], text[0..n]);
    len.* = n;
}

fn indexOfLocked(self: *TaskManager, id: u64) ?usize {
    for (self.tasks[0..self.count], 0..) |*t, i| {
        if (t.id == id) return i;
    }
    return null;
}

fn removeAtLocked(self: *TaskManager, idx: usize) void {
    for (idx..self.count - 1) |i| self.tasks[i] = self.tasks[i + 1];
    self.count -= 1;
}

/// Drop the oldest finished task to free a slot. Returns true on success.
fn dropOldestFinishedLocked(self: *TaskManager) bool {
    for (self.tasks[0..self.count], 0..) |*t, i| {
        if (t.isFinished()) {
            self.removeAtLocked(i);
            return true;
        }
    }
    return false;
}

// ── Creation ──────────────────────────────────────────────────────────────────

/// Create a task with the given initial status. Returns its id, or 0 if the
/// registry is full of active tasks.
pub fn create(self: *TaskManager, kind: Kind, label: []const u8, status: Status) u64 {
    self.guard.lock();
    defer self.guard.unlock();

    if (self.count >= MAX_TASKS and !self.dropOldestFinishedLocked()) return 0;

    const id = self.next_id;
    self.next_id += 1;

    var t = Task{ .id = id, .kind = kind, .status = status };
    setBuf(&t.label_buf, &t.label_len, label);
    self.tasks[self.count] = t;
    self.count += 1;
    return id;
}

/// Create a task already in the `running` state.
pub fn begin(self: *TaskManager, kind: Kind, label: []const u8) u64 {
    return self.create(kind, label, .running);
}

/// Create a task in the `queued` state (waiting to start).
pub fn enqueue(self: *TaskManager, kind: Kind, label: []const u8) u64 {
    return self.create(kind, label, .queued);
}

// ── Mutation ──────────────────────────────────────────────────────────────────

/// Move a queued task to running. No-op for unknown or already-running tasks.
pub fn start(self: *TaskManager, id: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| {
        if (self.tasks[i].status == .queued) self.tasks[i].status = .running;
    }
}

/// Update a task's progress fraction (0..1) and, if non-empty, its note.
/// A queued task transitions to running on its first progress update.
pub fn setProgress(self: *TaskManager, id: u64, fraction: f32, note: []const u8) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| {
        var t = &self.tasks[i];
        if (t.status == .queued) t.status = .running;
        t.progress = std.math.clamp(fraction, 0, 1);
        if (note.len > 0) setBuf(&t.note_buf, &t.note_len, note);
    }
}

/// Request cooperative cancellation. The running operation observes this via
/// `Progress.cancelled` and should abort, then call `cancel` to finalise.
pub fn requestCancel(self: *TaskManager, id: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| self.tasks[i].cancel_requested = true;
}

/// Whether cancellation has been requested for the given task.
pub fn isCancelRequested(self: *TaskManager, id: u64) bool {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| return self.tasks[i].cancel_requested;
    return false;
}

fn finishLocked(self: *TaskManager, id: u64, status: Status, note: []const u8) void {
    if (self.indexOfLocked(id)) |i| {
        var t = &self.tasks[i];
        t.status = status;
        if (status == .completed) t.progress = 1;
        if (note.len > 0) setBuf(&t.note_buf, &t.note_len, note);
    }
}

/// Mark a task completed (progress is forced to 1).
pub fn complete(self: *TaskManager, id: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    self.finishLocked(id, .completed, "");
}

/// Mark a task failed with an explanatory message.
pub fn fail(self: *TaskManager, id: u64, message: []const u8) void {
    self.guard.lock();
    defer self.guard.unlock();
    self.finishLocked(id, .failed, message);
}

/// Mark a task cancelled (terminal). Use after observing a cancel request.
pub fn cancel(self: *TaskManager, id: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    self.finishLocked(id, .cancelled, "");
}

// ── Queries ───────────────────────────────────────────────────────────────────

/// Snapshot a single task by id, or null if not found.
pub fn get(self: *TaskManager, id: u64) ?Task {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| return self.tasks[i];
    return null;
}

/// Copy all tasks into `buf`, returning the number written. The UI calls this
/// once per frame to render a consistent snapshot without holding the lock.
pub fn snapshot(self: *TaskManager, buf: []Task) usize {
    self.guard.lock();
    defer self.guard.unlock();
    const n = @min(buf.len, self.count);
    @memcpy(buf[0..n], self.tasks[0..n]);
    return n;
}

/// Number of tasks that have not reached a terminal state.
pub fn activeCount(self: *TaskManager) usize {
    self.guard.lock();
    defer self.guard.unlock();
    var n: usize = 0;
    for (self.tasks[0..self.count]) |*t| {
        if (t.isActive()) n += 1;
    }
    return n;
}

/// Total number of tracked tasks (active + finished).
pub fn totalCount(self: *TaskManager) usize {
    self.guard.lock();
    defer self.guard.unlock();
    return self.count;
}

/// Remove all tasks that have reached a terminal state.
pub fn clearFinished(self: *TaskManager) void {
    self.guard.lock();
    defer self.guard.unlock();
    var i: usize = 0;
    while (i < self.count) {
        if (self.tasks[i].isFinished()) self.removeAtLocked(i) else i += 1;
    }
}

// ── Progress binding ──────────────────────────────────────────────────────────

fn progReport(ctx: ?*anyopaque, id: u64, fraction: f32, note: []const u8) void {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    self.setProgress(id, fraction, note);
}

fn progCancelled(ctx: ?*anyopaque, id: u64) bool {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    return self.isCancelRequested(id);
}

const prog_vtable = Progress.VTable{
    .report = progReport,
    .cancelled = progCancelled,
};

/// Build a `Progress` value bound to the given task. Pass it to operations so
/// their progress reports and cancellation polls flow into this manager.
pub fn progressFor(self: *TaskManager, id: u64) Progress {
    return .{ .ctx = self, .id = id, .vtable = &prog_vtable };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "begin assigns increasing ids and running status" {
    var tm = TaskManager.init();
    const a = tm.begin(.import, "Import A");
    const b = tm.begin(.build, "Build B");
    try std.testing.expect(a != 0 and b != 0 and b > a);

    const ta = tm.get(a).?;
    try std.testing.expectEqual(Status.running, ta.status);
    try std.testing.expectEqualStrings("Import A", ta.label());
    try std.testing.expectEqual(Kind.import, ta.kind);
}

test "queued task starts on first progress update" {
    var tm = TaskManager.init();
    const id = tm.enqueue(.compile, "Compile");
    try std.testing.expectEqual(Status.queued, tm.get(id).?.status);
    tm.setProgress(id, 0.25, "step 1");
    const t = tm.get(id).?;
    try std.testing.expectEqual(Status.running, t.status);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), t.progress, 0.0001);
    try std.testing.expectEqualStrings("step 1", t.note());
}

test "progress is clamped to 0..1" {
    var tm = TaskManager.init();
    const id = tm.begin(.generic, "x");
    tm.setProgress(id, 5.0, "");
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), tm.get(id).?.progress, 0.0001);
    tm.setProgress(id, -3.0, "");
    try std.testing.expectApproxEqAbs(@as(f32, 0.0), tm.get(id).?.progress, 0.0001);
}

test "complete/fail/cancel reach terminal states" {
    var tm = TaskManager.init();
    const a = tm.begin(.generic, "a");
    const b = tm.begin(.generic, "b");
    const c = tm.begin(.generic, "c");

    tm.complete(a);
    tm.fail(b, "boom");
    tm.cancel(c);

    const ta = tm.get(a).?;
    try std.testing.expectEqual(Status.completed, ta.status);
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), ta.progress, 0.0001);
    try std.testing.expect(ta.isFinished());

    const tb = tm.get(b).?;
    try std.testing.expectEqual(Status.failed, tb.status);
    try std.testing.expectEqualStrings("boom", tb.note());

    try std.testing.expectEqual(Status.cancelled, tm.get(c).?.status);
}

test "cancellation request flows through progressFor" {
    var tm = TaskManager.init();
    const id = tm.begin(.build, "Build");
    const p = tm.progressFor(id);

    try std.testing.expect(!p.cancelled());
    tm.requestCancel(id);
    try std.testing.expect(p.cancelled());

    // Reporting through the bound Progress updates the task.
    p.report(0.5, "half");
    const t = tm.get(id).?;
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), t.progress, 0.0001);
    try std.testing.expectEqualStrings("half", t.note());
}

test "clearFinished keeps active tasks only" {
    var tm = TaskManager.init();
    const a = tm.begin(.generic, "a");
    const b = tm.begin(.generic, "b");
    tm.complete(a);

    try std.testing.expectEqual(@as(usize, 2), tm.totalCount());
    try std.testing.expectEqual(@as(usize, 1), tm.activeCount());

    tm.clearFinished();
    try std.testing.expectEqual(@as(usize, 1), tm.totalCount());
    try std.testing.expect(tm.get(a) == null);
    try std.testing.expect(tm.get(b) != null);
}

test "snapshot copies all tasks" {
    var tm = TaskManager.init();
    _ = tm.begin(.generic, "a");
    _ = tm.begin(.generic, "b");
    var buf: [MAX_TASKS]Task = undefined;
    try std.testing.expectEqual(@as(usize, 2), tm.snapshot(&buf));
}

test "full registry reclaims finished slots" {
    var tm = TaskManager.init();
    var i: usize = 0;
    while (i < MAX_TASKS) : (i += 1) _ = tm.begin(.generic, "t");
    // All active: no slot available.
    try std.testing.expectEqual(@as(u64, 0), tm.begin(.generic, "overflow"));

    // Finish one, then a new task reclaims its slot.
    tm.complete(1);
    try std.testing.expect(tm.begin(.generic, "reclaimed") != 0);
}
