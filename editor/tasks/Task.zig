//! Data model for background tasks: lifecycle status, category, the editor
//! capabilities a task holds while it runs, and how duplicate submissions are
//! resolved. `TaskManager` owns the storage; this file owns the shape.
const std = @import("std");

/// Fixed capacities — tasks live in a flat array, so every string is inline.
pub const MAX_LABEL = 96;
pub const MAX_NOTE = 128;
pub const MAX_KEY = 64;
/// Dependencies one task may declare, sized for the deepest chain the editor
/// builds (build ← compile ← import).
pub const MAX_DEPS = 4;

pub const Status = enum {
    /// Declared, but at least one dependency has not reached a terminal state.
    blocked,
    /// Ready to run, waiting for a free worker slot.
    queued,
    running,
    completed,
    failed,
    cancelled,

    pub fn text(self: Status) []const u8 {
        return switch (self) {
            .blocked => "Waiting",
            .queued => "Queued",
            .running => "Running",
            .completed => "Completed",
            .failed => "Failed",
            .cancelled => "Cancelled",
        };
    }

    /// True once the task can no longer change state.
    pub fn isTerminal(self: Status) bool {
        return switch (self) {
            .completed, .failed, .cancelled => true,
            .blocked, .queued, .running => false,
        };
    }
};

pub const Kind = enum {
    generic,
    scan,
    import,
    compile,
    build,
    package,

    pub fn text(self: Kind) []const u8 {
        return switch (self) {
            .generic => "Task",
            .scan => "Scan",
            .import => "Import",
            .compile => "Compile",
            .build => "Build",
            .package => "Package",
        };
    }

    /// Ranking used to pick which of several parallel tasks the compact task
    /// bar summarises — the most disruptive operation wins the one visible row.
    pub fn priority(self: Kind) u8 {
        return switch (self) {
            .build => 5,
            .package => 4,
            .compile => 3,
            .import => 2,
            .scan => 1,
            .generic => 0,
        };
    }
};

/// Editor capabilities a task holds while it is active. Actions are disabled
/// per capability rather than wholesale, so a long script compile still leaves
/// scene editing and saving available.
pub const Locks = packed struct(u8) {
    /// Asset artifacts and the asset database are being rewritten.
    assets: bool = false,
    /// Compiled user-script binaries and reflection data are stale.
    scripts: bool = false,
    /// Scene contents are being mutated from a worker.
    scene: bool = false,
    /// Project-level files (build output, settings) are being written.
    project: bool = false,
    _reserved: u4 = 0,

    pub const none: Locks = .{};

    pub fn any(self: Locks) bool {
        return @as(u8, @bitCast(self)) != 0;
    }

    pub fn merge(self: Locks, other: Locks) Locks {
        return @bitCast(@as(u8, @bitCast(self)) | @as(u8, @bitCast(other)));
    }

    /// True when the two sets share at least one capability.
    pub fn overlaps(self: Locks, other: Locks) bool {
        return (@as(u8, @bitCast(self)) & @as(u8, @bitCast(other))) != 0;
    }
};

/// How a submission is resolved when a task with the same key is already active.
pub const Policy = enum {
    /// Always create a new task, even alongside an identical one.
    queue,
    /// Reuse the in-flight task and flag it to run once more when it lands, so
    /// a burst of clicks costs exactly one extra run.
    coalesce,
    /// Ignore the submission and return the in-flight task's id.
    drop,
    /// Cancel the in-flight task and queue a fresh one behind it.
    restart,
};

pub const Task = struct {
    id: u64 = 0,
    /// Owning task, or 0 for a root. Children roll up into the parent's
    /// aggregate progress (see `TaskTree`).
    parent_id: u64 = 0,
    kind: Kind = .generic,
    status: Status = .queued,
    /// Own completion fraction in 0..1; ignored once `units_total` is set.
    progress: f32 = 0,
    /// Share of the parent's aggregate this child accounts for. Phases of
    /// wildly different cost (a 2 s package vs. a 4 min compile) would
    /// otherwise make the parent bar lie.
    weight: f32 = 1,
    /// Item counters for batch work ("142 / 1035"), preferred over `progress`.
    units_done: u64 = 0,
    units_total: u64 = 0,
    /// Total child weight the owner intends to open, declared up front. Without
    /// it the aggregate divides by the children opened *so far*, so opening a
    /// heavy phase drags the parent backwards — and a bar that walks backwards
    /// reads as a bug. Zero means "however many children turn up".
    planned_weight: f32 = 0,
    cancel_requested: bool = false,
    /// Set when a `.coalesce` duplicate arrived mid-flight; the owner reruns
    /// the operation once on completion instead of queueing one job per click.
    rerun_requested: bool = false,
    locks: Locks = .none,
    deps: [MAX_DEPS]u64 = @splat(0),
    dep_count: u8 = 0,
    /// Wall-clock milliseconds in the caller's own clock domain, stamped by
    /// `TaskManager.tick`. Optional rather than zero-sentinelled so a clock
    /// that legitimately reads 0 does not look unstamped.
    started_at_ms: ?i64 = null,
    finished_at_ms: ?i64 = null,
    label_buf: [MAX_LABEL]u8 = undefined,
    label_len: usize = 0,
    note_buf: [MAX_NOTE]u8 = undefined,
    note_len: usize = 0,
    key_buf: [MAX_KEY]u8 = undefined,
    key_len: usize = 0,

    pub fn label(self: *const Task) []const u8 {
        return self.label_buf[0..self.label_len];
    }

    pub fn note(self: *const Task) []const u8 {
        return self.note_buf[0..self.note_len];
    }

    /// Duplicate-detection key; falls back to the label when none was given.
    pub fn key(self: *const Task) []const u8 {
        return if (self.key_len > 0) self.key_buf[0..self.key_len] else self.label();
    }

    pub fn dependencies(self: *const Task) []const u64 {
        return self.deps[0..self.dep_count];
    }

    /// True once the task has reached a terminal state.
    pub fn isFinished(self: *const Task) bool {
        return self.status.isTerminal();
    }

    pub fn isActive(self: *const Task) bool {
        return !self.isFinished();
    }

    /// Completion fraction in 0..1, from item counters when available.
    pub fn fraction(self: *const Task) f32 {
        if (self.units_total == 0) return self.progress;
        const done: f32 = @floatFromInt(@min(self.units_done, self.units_total));
        return done / @as(f32, @floatFromInt(self.units_total));
    }

    /// Milliseconds spent so far, or in total once finished. Zero until
    /// `TaskManager.tick` has stamped the start.
    pub fn elapsedMs(self: *const Task, now_ms: i64) i64 {
        const start = self.started_at_ms orelse return 0;
        return @max(0, (self.finished_at_ms orelse now_ms) - start);
    }
};

test "status terminality" {
    try std.testing.expect(Status.completed.isTerminal());
    try std.testing.expect(Status.failed.isTerminal());
    try std.testing.expect(Status.cancelled.isTerminal());
    try std.testing.expect(!Status.blocked.isTerminal());
    try std.testing.expect(!Status.queued.isTerminal());
    try std.testing.expect(!Status.running.isTerminal());
}

test "locks compose and compare" {
    const a = Locks{ .assets = true };
    const s = Locks{ .scripts = true };
    try std.testing.expect(a.any());
    try std.testing.expect(!Locks.none.any());
    try std.testing.expect(!a.overlaps(s));
    try std.testing.expect(a.merge(s).overlaps(s));
    try std.testing.expect(a.merge(s).overlaps(a));
}

test "unit counters drive the fraction" {
    var t = Task{ .progress = 0.9 };
    try std.testing.expectApproxEqAbs(@as(f32, 0.9), t.fraction(), 0.0001);
    t.units_total = 4;
    t.units_done = 1;
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), t.fraction(), 0.0001);
    // Overshooting the total clamps rather than exceeding 1.
    t.units_done = 9;
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), t.fraction(), 0.0001);
}

test "elapsed needs a stamped start" {
    var t = Task{};
    try std.testing.expectEqual(@as(i64, 0), t.elapsedMs(5_000));
    t.started_at_ms = 1_000;
    try std.testing.expectEqual(@as(i64, 4_000), t.elapsedMs(5_000));
    t.finished_at_ms = 2_500;
    try std.testing.expectEqual(@as(i64, 1_500), t.elapsedMs(5_000));
    // A clock reading exactly zero is a real timestamp, not "unstamped".
    t.started_at_ms = 0;
    t.finished_at_ms = null;
    try std.testing.expectEqual(@as(i64, 5_000), t.elapsedMs(5_000));
}
