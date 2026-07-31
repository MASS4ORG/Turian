//! Thread-safe registry of long-running operations (asset import, script
//! compilation, game build) with progress reporting, parent/child nesting,
//! dependency ordering, capability locks and cancellation.
//! Tasks are stored in a fixed array; a spinlock guards all access.
const std = @import("std");
const Progress = @import("Progress.zig").Progress;
const task_mod = @import("Task.zig");

pub const Task = task_mod.Task;
pub const Status = task_mod.Status;
pub const Kind = task_mod.Kind;
pub const Locks = task_mod.Locks;
pub const Policy = task_mod.Policy;

/// Maximum number of tasks tracked at once. Oldest finished tasks are reclaimed
/// when the registry is full and a new task is created.
pub const MAX_TASKS = 64;
pub const MAX_LABEL = task_mod.MAX_LABEL;
pub const MAX_NOTE = task_mod.MAX_NOTE;

/// How long a *completed* root lingers before `tick` reclaims it. Failed and
/// cancelled tasks are sticky — they stay until the user clears them, because
/// they carry a diagnosis nobody has read yet.
pub const DEFAULT_AUTO_CLEAR_MS: i64 = 5_000;

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
/// Retention for completed roots; set to 0 to keep them until cleared.
auto_clear_ms: i64 = DEFAULT_AUTO_CLEAR_MS,

const TaskManager = @This();

pub fn init() TaskManager {
    return .{};
}

// ── Internal helpers (caller must hold the guard) ─────────────────────────────

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

/// First active task sharing `key`, ignoring children (only roots are
/// user-submitted and therefore dedupe candidates).
fn findActiveByKeyLocked(self: *TaskManager, key: []const u8) ?*Task {
    if (key.len == 0) return null;
    for (self.tasks[0..self.count]) |*t| {
        if (t.parent_id == 0 and t.isActive() and std.mem.eql(u8, t.key(), key)) return t;
    }
    return null;
}

// ── Creation ──────────────────────────────────────────────────────────────────

/// Everything a submission may specify. Only `kind` and `label` are usually set.
pub const Spec = struct {
    kind: Kind = .generic,
    label: []const u8 = "",
    /// Duplicate-detection key; defaults to `label` when empty.
    key: []const u8 = "",
    policy: Policy = .queue,
    /// State the task starts in. `.blocked` is implied when `deps` is non-empty.
    status: Status = .running,
    parent_id: u64 = 0,
    locks: Locks = .none,
    /// Ids that must reach a terminal state before this task may run.
    deps: []const u64 = &.{},
    weight: f32 = 1,
};

/// Create a task, applying `policy` against any active task with the same key.
/// Returns the new id, the reused id for `.coalesce`/`.drop`, or 0 if the
/// registry is full of active tasks.
pub fn submit(self: *TaskManager, spec: Spec) u64 {
    self.guard.lock();
    defer self.guard.unlock();

    if (spec.policy != .queue) {
        const key = if (spec.key.len > 0) spec.key else spec.label;
        if (self.findActiveByKeyLocked(key)) |existing| switch (spec.policy) {
            .coalesce => {
                existing.rerun_requested = true;
                return existing.id;
            },
            .drop => return existing.id,
            .restart => existing.cancel_requested = true,
            .queue => unreachable,
        };
    }

    if (self.count >= MAX_TASKS and !self.dropOldestFinishedLocked()) return 0;

    const id = self.next_id;
    self.next_id += 1;

    var t = Task{
        .id = id,
        .parent_id = spec.parent_id,
        .kind = spec.kind,
        .status = if (spec.deps.len > 0) .blocked else spec.status,
        .locks = spec.locks,
        .weight = spec.weight,
    };
    setBuf(&t.label_buf, &t.label_len, spec.label);
    setBuf(&t.key_buf, &t.key_len, spec.key);
    t.dep_count = @intCast(@min(spec.deps.len, task_mod.MAX_DEPS));
    @memcpy(t.deps[0..t.dep_count], spec.deps[0..t.dep_count]);

    self.tasks[self.count] = t;
    self.count += 1;
    return id;
}

/// Create a task already in the `running` state.
pub fn begin(self: *TaskManager, kind: Kind, label: []const u8) u64 {
    return self.submit(.{ .kind = kind, .label = label, .status = .running });
}

/// Create a task in the `queued` state (waiting to start).
pub fn enqueue(self: *TaskManager, kind: Kind, label: []const u8) u64 {
    return self.submit(.{ .kind = kind, .label = label, .status = .queued });
}

/// Create a child of `parent_id`, contributing `weight` to its aggregate.
pub fn beginChild(self: *TaskManager, parent_id: u64, kind: Kind, label: []const u8, weight: f32) u64 {
    return self.submit(.{
        .kind = kind,
        .label = label,
        .status = .running,
        .parent_id = parent_id,
        .weight = weight,
    });
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

/// Publish item counters for batch work. Once set they drive the task's
/// fraction, so a 1000-asset import reports honest progress without spawning
/// 1000 child tasks.
pub fn setUnits(self: *TaskManager, id: u64, done: u64, total: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| {
        var t = &self.tasks[i];
        if (t.status == .queued) t.status = .running;
        t.units_done = done;
        t.units_total = total;
    }
}

/// Declare the total child weight this task will open, so its aggregate has a
/// stable denominator from the start rather than one that grows per phase.
pub fn setPlannedWeight(self: *TaskManager, id: u64, total_weight: f32) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| self.tasks[i].planned_weight = @max(total_weight, 0);
}

/// Request cooperative cancellation of a task and everything under it. The
/// running operation observes this via `Progress.cancelled` and should abort,
/// then call `cancel` to finalise.
pub fn requestCancel(self: *TaskManager, id: u64) void {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| self.tasks[i].cancel_requested = true;
    for (self.tasks[0..self.count]) |*t| {
        if (t.parent_id == id) t.cancel_requested = true;
    }
}

/// Whether cancellation has been requested for the given task.
pub fn isCancelRequested(self: *TaskManager, id: u64) bool {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| return self.tasks[i].cancel_requested;
    return false;
}

/// Consume a pending rerun flag set by a `.coalesce` duplicate. Owners call
/// this when a task lands, and relaunch once if it returns true.
pub fn takeRerunRequest(self: *TaskManager, id: u64) bool {
    self.guard.lock();
    defer self.guard.unlock();
    if (self.indexOfLocked(id)) |i| {
        const pending = self.tasks[i].rerun_requested;
        self.tasks[i].rerun_requested = false;
        return pending;
    }
    return false;
}

fn finishLocked(self: *TaskManager, id: u64, status: Status, note: []const u8) void {
    if (self.indexOfLocked(id)) |i| {
        var t = &self.tasks[i];
        t.status = status;
        if (status == .completed) {
            t.progress = 1;
            t.units_done = t.units_total;
        }
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

/// Union of the capabilities held by every active task. Callers gate individual
/// UI actions on this instead of blocking the whole editor.
pub fn activeLocks(self: *TaskManager) Locks {
    self.guard.lock();
    defer self.guard.unlock();
    var held: Locks = .none;
    for (self.tasks[0..self.count]) |*t| {
        if (t.isActive()) held = held.merge(t.locks);
    }
    return held;
}

/// The active task holding any of `wanted`, for naming the blocker in the UI.
pub fn lockOwner(self: *TaskManager, wanted: Locks) ?Task {
    self.guard.lock();
    defer self.guard.unlock();
    for (self.tasks[0..self.count]) |*t| {
        if (t.isActive() and t.locks.overlaps(wanted)) return t.*;
    }
    return null;
}

/// Id of the next task that is ready to run — `queued`, in submission order.
/// Blocked tasks become queued once `tick` resolves their dependencies.
pub fn nextReady(self: *TaskManager) ?u64 {
    self.guard.lock();
    defer self.guard.unlock();
    for (self.tasks[0..self.count]) |*t| {
        if (t.status == .queued) return t.id;
    }
    return null;
}

// ── Maintenance ───────────────────────────────────────────────────────────────

/// Advance bookkeeping that needs a clock and a single-threaded moment: stamp
/// start/finish times, release dependency-blocked tasks, cascade dependency
/// failures, and reclaim completed roots once their retention elapses.
/// Call once per frame with a monotonic millisecond timestamp.
pub fn tick(self: *TaskManager, now_ms: i64) void {
    self.guard.lock();
    defer self.guard.unlock();

    self.resolveDependenciesLocked();

    for (self.tasks[0..self.count]) |*t| {
        if (t.started_at_ms == null and (t.status == .running or t.isFinished())) t.started_at_ms = now_ms;
        if (t.finished_at_ms == null and t.isFinished()) t.finished_at_ms = now_ms;
    }

    if (self.auto_clear_ms > 0) self.reclaimLocked(now_ms);
}

/// Release dependency-blocked tasks whose dependencies have landed, and cancel
/// those whose dependencies broke, without the rest of `tick`'s bookkeeping.
/// For synchronous drains that have no frame clock to advance.
pub fn resolveDependencies(self: *TaskManager) void {
    self.guard.lock();
    defer self.guard.unlock();
    self.resolveDependenciesLocked();
}

/// Promote blocked tasks whose dependencies all landed, and cancel those whose
/// dependencies failed — a build must not run on a half-imported project.
fn resolveDependenciesLocked(self: *TaskManager) void {
    for (0..self.count) |i| {
        if (self.tasks[i].status != .blocked) continue;
        var all_done = true;
        var any_broken = false;
        for (self.tasks[i].dependencies()) |dep_id| {
            const dep = if (self.indexOfLocked(dep_id)) |j| self.tasks[j] else continue;
            switch (dep.status) {
                .completed => {},
                .failed, .cancelled => any_broken = true,
                else => all_done = false,
            }
        }
        if (any_broken) {
            self.tasks[i].status = .cancelled;
            setBuf(&self.tasks[i].note_buf, &self.tasks[i].note_len, "Dependency did not finish");
        } else if (all_done) {
            self.tasks[i].status = .queued;
        }
    }
}

/// Drop completed roots past their retention, then any child left orphaned.
fn reclaimLocked(self: *TaskManager, now_ms: i64) void {
    var i: usize = 0;
    while (i < self.count) {
        const t = &self.tasks[i];
        const expired = t.parent_id == 0 and t.status == .completed and
            now_ms - (t.finished_at_ms orelse now_ms) >= self.auto_clear_ms;
        if (expired) self.removeAtLocked(i) else i += 1;
    }
    i = 0;
    while (i < self.count) {
        const t = &self.tasks[i];
        if (t.parent_id != 0 and self.indexOfLocked(t.parent_id) == null) {
            self.removeAtLocked(i);
        } else i += 1;
    }
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

fn progBeginChild(ctx: ?*anyopaque, parent_id: u64, kind: Kind, label: []const u8, weight: f32) u64 {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    return self.beginChild(parent_id, kind, label, weight);
}

fn progEndChild(ctx: ?*anyopaque, id: u64, ok: bool) void {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    if (ok) self.complete(id) else self.fail(id, "");
}

fn progUnits(ctx: ?*anyopaque, id: u64, done: u64, total: u64) void {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    self.setUnits(id, done, total);
}

fn progPlan(ctx: ?*anyopaque, id: u64, total_weight: f32) void {
    const self: *TaskManager = @ptrCast(@alignCast(ctx.?));
    self.setPlannedWeight(id, total_weight);
}

const prog_vtable = Progress.VTable{
    .report = progReport,
    .cancelled = progCancelled,
    .beginChild = progBeginChild,
    .endChild = progEndChild,
    .units = progUnits,
    .planChildren = progPlan,
};

/// Build a `Progress` value bound to the given task. Pass it to operations so
/// their progress reports, sub-steps and cancellation polls flow into this
/// manager.
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

test "cancellation request flows through progressFor and reaches children" {
    var tm = TaskManager.init();
    const id = tm.begin(.build, "Build");
    const child = tm.beginChild(id, .compile, "Compile game", 1);
    const p = tm.progressFor(id);

    try std.testing.expect(!p.cancelled());
    tm.requestCancel(id);
    try std.testing.expect(p.cancelled());
    try std.testing.expect(tm.isCancelRequested(child));

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

test "coalesce reuses the in-flight task and flags one rerun" {
    var tm = TaskManager.init();
    const spec = Spec{ .kind = .import, .label = "Reimport assets", .policy = .coalesce };
    const first = tm.submit(spec);
    const second = tm.submit(spec);
    const third = tm.submit(spec);

    try std.testing.expectEqual(first, second);
    try std.testing.expectEqual(first, third);
    try std.testing.expectEqual(@as(usize, 1), tm.totalCount());
    try std.testing.expect(tm.takeRerunRequest(first));
    // The flag is consumed, so a burst of clicks costs exactly one rerun.
    try std.testing.expect(!tm.takeRerunRequest(first));

    // Once the original lands, a resubmission starts a genuinely new task.
    tm.complete(first);
    try std.testing.expect(tm.submit(spec) != first);
}

test "drop ignores duplicates, restart cancels the incumbent" {
    var tm = TaskManager.init();
    const a = tm.submit(.{ .kind = .build, .label = "Build game", .policy = .drop });
    try std.testing.expectEqual(a, tm.submit(.{ .kind = .build, .label = "Build game", .policy = .drop }));
    try std.testing.expectEqual(@as(usize, 1), tm.totalCount());

    const b = tm.submit(.{ .kind = .build, .label = "Build game", .policy = .restart });
    try std.testing.expect(b != a);
    try std.testing.expect(tm.get(a).?.cancel_requested);
}

test "explicit key separates tasks sharing a label" {
    var tm = TaskManager.init();
    const a = tm.submit(.{ .kind = .import, .label = "Import assets", .key = "import:proj-a", .policy = .coalesce });
    const b = tm.submit(.{ .kind = .import, .label = "Import assets", .key = "import:proj-b", .policy = .coalesce });
    try std.testing.expect(a != b);
}

test "dependencies block, then release on completion" {
    var tm = TaskManager.init();
    const imp = tm.begin(.import, "Import assets");
    const compile = tm.submit(.{ .kind = .compile, .label = "Compile scripts", .deps = &.{imp} });

    try std.testing.expectEqual(Status.blocked, tm.get(compile).?.status);
    tm.tick(0);
    try std.testing.expectEqual(Status.blocked, tm.get(compile).?.status);
    try std.testing.expect(tm.nextReady() == null);

    tm.complete(imp);
    tm.tick(1);
    try std.testing.expectEqual(Status.queued, tm.get(compile).?.status);
    try std.testing.expectEqual(compile, tm.nextReady().?);
}

test "a failed dependency cancels its dependents" {
    var tm = TaskManager.init();
    const imp = tm.begin(.import, "Import assets");
    const build = tm.submit(.{ .kind = .build, .label = "Build game", .deps = &.{imp} });

    tm.fail(imp, "disk error");
    tm.tick(0);
    const t = tm.get(build).?;
    try std.testing.expectEqual(Status.cancelled, t.status);
    try std.testing.expectEqualStrings("Dependency did not finish", t.note());
}

test "locks report holders and are released on completion" {
    var tm = TaskManager.init();
    const id = tm.submit(.{ .kind = .compile, .label = "Compile scripts", .locks = .{ .scripts = true } });

    try std.testing.expect(tm.activeLocks().overlaps(.{ .scripts = true }));
    try std.testing.expect(!tm.activeLocks().overlaps(.{ .assets = true }));
    try std.testing.expectEqualStrings("Compile scripts", tm.lockOwner(.{ .scripts = true }).?.label());

    tm.complete(id);
    try std.testing.expect(!tm.activeLocks().any());
    try std.testing.expect(tm.lockOwner(.{ .scripts = true }) == null);
}

test "tick stamps timings and auto-clears completed roots" {
    var tm = TaskManager.init();
    tm.auto_clear_ms = 1_000;
    const done = tm.begin(.import, "Import assets");
    const child = tm.beginChild(done, .import, "Textures", 1);
    const broke = tm.begin(.build, "Build game");

    tm.tick(100);
    try std.testing.expectEqual(@as(?i64, 100), tm.get(done).?.started_at_ms);
    try std.testing.expectEqual(@as(i64, 400), tm.get(done).?.elapsedMs(500));

    tm.complete(done);
    tm.complete(child);
    tm.fail(broke, "compile error");
    tm.tick(600);
    try std.testing.expectEqual(@as(?i64, 600), tm.get(done).?.finished_at_ms);
    // Elapsed freezes at the finish, ignoring the clock moving on.
    try std.testing.expectEqual(@as(i64, 500), tm.get(done).?.elapsedMs(9_000));
    try std.testing.expectEqual(@as(usize, 3), tm.totalCount());

    // Retention elapses: the completed root and its child go, the failure stays.
    tm.tick(1_700);
    try std.testing.expectEqual(@as(usize, 1), tm.totalCount());
    try std.testing.expect(tm.get(done) == null);
    try std.testing.expect(tm.get(child) == null);
    try std.testing.expect(tm.get(broke) != null);
}

test "auto-clear is disabled when the retention is zero" {
    var tm = TaskManager.init();
    tm.auto_clear_ms = 0;
    const id = tm.begin(.generic, "a");
    tm.complete(id);
    tm.tick(0);
    tm.tick(1_000_000);
    try std.testing.expectEqual(@as(usize, 1), tm.totalCount());
}

test "unit counters drive completion and are settled on complete" {
    var tm = TaskManager.init();
    const id = tm.begin(.import, "Import assets");
    tm.setUnits(id, 3, 12);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), tm.get(id).?.fraction(), 0.0001);
    tm.complete(id);
    const t = tm.get(id).?;
    try std.testing.expectEqual(@as(u64, 12), t.units_done);
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), t.fraction(), 0.0001);
}

test "progress vtable opens and closes child tasks" {
    var tm = TaskManager.init();
    const root = tm.begin(.build, "Build game");
    const p = tm.progressFor(root);

    const phase = p.child(.package, "Packaging assets", 2);
    try std.testing.expect(phase.id != 0);
    phase.units(2, 8);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), tm.get(phase.id).?.fraction(), 0.0001);
    try std.testing.expectApproxEqAbs(@as(f32, 2.0), tm.get(phase.id).?.weight, 0.0001);

    phase.finish(true);
    try std.testing.expectEqual(Status.completed, tm.get(phase.id).?.status);
    try std.testing.expectEqual(root, tm.get(phase.id).?.parent_id);
}

test "planned weight is declared through the progress vtable" {
    var tm = TaskManager.init();
    const root = tm.begin(.build, "Build game");
    const p = tm.progressFor(root);

    p.plan(17);
    try std.testing.expectApproxEqAbs(@as(f32, 17), tm.get(root).?.planned_weight, 0.0001);
    // A negative plan is meaningless and would invert the aggregate.
    p.plan(-4);
    try std.testing.expectApproxEqAbs(@as(f32, 0), tm.get(root).?.planned_weight, 0.0001);
}
