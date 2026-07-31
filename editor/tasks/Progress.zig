//! Progress + cancellation interface for long-running editor operations.
//! `report` publishes a 0..1 fraction; `cancelled` polls for early abort;
//! `child` and `units` let an operation describe its shape (nested phases,
//! item counters) without depending on the task registry that renders it.
//! The default `none` is a no-op so operations run unobserved by default.
const std = @import("std");
const Kind = @import("Task.zig").Kind;

pub const Progress = struct {
    ctx: ?*anyopaque = null,
    /// Opaque task identifier, interpreted by the vtable implementation.
    id: u64 = 0,
    vtable: ?*const VTable = null,

    pub const VTable = struct {
        report: *const fn (ctx: ?*anyopaque, id: u64, fraction: f32, note: []const u8) void,
        cancelled: *const fn (ctx: ?*anyopaque, id: u64) bool,
        /// Open a nested sub-operation, returning its id (0 if refused).
        beginChild: ?*const fn (ctx: ?*anyopaque, parent_id: u64, kind: Kind, label: []const u8, weight: f32) u64 = null,
        /// Close a sub-operation opened by `beginChild`.
        endChild: ?*const fn (ctx: ?*anyopaque, id: u64, ok: bool) void = null,
        /// Publish item counters for batch work.
        units: ?*const fn (ctx: ?*anyopaque, id: u64, done: u64, total: u64) void = null,
        /// Declare the total child weight this operation will open.
        planChildren: ?*const fn (ctx: ?*anyopaque, id: u64, total_weight: f32) void = null,
    };

    /// No-op progress sink — discards reports and never reports cancellation.
    pub const none: Progress = .{};

    /// Publish a completion fraction (clamped to 0..1) and a short status note.
    /// `note` may be empty to update only the fraction.
    pub fn report(self: Progress, fraction: f32, note: []const u8) void {
        if (self.vtable) |vt| vt.report(self.ctx, self.id, std.math.clamp(fraction, 0, 1), note);
    }

    /// Returns true once cancellation has been requested for this operation.
    /// Operations should poll this at convenient checkpoints and abort cleanly.
    pub fn cancelled(self: Progress) bool {
        return if (self.vtable) |vt| vt.cancelled(self.ctx, self.id) else false;
    }

    /// Publish "done of total" counters for batch work. Preferred over `report`
    /// when the operation knows its item count, since the display can then show
    /// both a bar and a meaningful "142 / 1035".
    pub fn units(self: Progress, done: u64, total: u64) void {
        const vt = self.vtable orelse return;
        const f = vt.units orelse return;
        f(self.ctx, self.id, done, total);
    }

    /// Declare the combined weight of every phase this operation will open,
    /// before opening the first. Without it the aggregate divides by the
    /// phases opened so far, so starting a heavy phase drags it backwards.
    pub fn plan(self: Progress, total_weight: f32) void {
        const vt = self.vtable orelse return;
        const f = vt.planChildren orelse return;
        f(self.ctx, self.id, total_weight);
    }

    /// Open a nested phase reporting into this operation's aggregate, where
    /// `weight` is its share of the parent (a 4-minute compile should not read
    /// as one quarter of a build that also does three 2-second steps).
    /// Close it with `finish`. Returns `none` when nesting is unsupported, so
    /// callers need no capability check.
    pub fn child(self: Progress, kind: Kind, label: []const u8, weight: f32) Progress {
        const vt = self.vtable orelse return .none;
        const begin = vt.beginChild orelse return .none;
        const id = begin(self.ctx, self.id, kind, label, weight);
        if (id == 0) return .none;
        return .{ .ctx = self.ctx, .id = id, .vtable = vt };
    }

    /// Close a phase opened by `child`, recording success or failure.
    pub fn finish(self: Progress, ok: bool) void {
        const vt = self.vtable orelse return;
        const f = vt.endChild orelse return;
        f(self.ctx, self.id, ok);
    }
};

test "none is a no-op and reports no cancellation" {
    const p = Progress.none;
    p.report(0.5, "ignored"); // must not crash
    p.units(1, 2);
    p.plan(4);
    p.finish(true);
    try std.testing.expect(!p.cancelled());
    try std.testing.expectEqual(@as(u64, 0), p.child(.build, "phase", 1).id);
}

test "optional vtable entries degrade to no-ops" {
    const Sink = struct {
        fn report(_: ?*anyopaque, _: u64, _: f32, _: []const u8) void {}
        fn cancelled(_: ?*anyopaque, _: u64) bool {
            return false;
        }
        const vtable = Progress.VTable{ .report = report, .cancelled = cancelled };
    };
    const p = Progress{ .id = 7, .vtable = &Sink.vtable };
    p.units(1, 2);
    p.plan(4);
    p.finish(true);
    // Nesting is unsupported here, so children collapse to the no-op sink.
    try std.testing.expectEqual(@as(u64, 0), p.child(.compile, "phase", 1).id);
}
