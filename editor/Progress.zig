//! Lightweight progress + cancellation interface for long-running editor
//! operations (asset import, script compilation, game build).
//!
//! An operation accepts a `Progress` value and calls `report` to publish a
//! 0..1 completion fraction plus a short status note, and `cancelled` to poll
//! whether the caller has requested an early abort. The default `none` value is
//! a no-op, so operations run unobserved without any special-casing.
//!
//! Implementations (e.g. `TaskManager.progressFor`) provide the vtable; the
//! `ctx`/`id` pair lets one backing store fan out to many concurrent tasks.
const std = @import("std");

pub const Progress = struct {
    ctx: ?*anyopaque = null,
    /// Opaque task identifier, interpreted by the vtable implementation.
    id: u64 = 0,
    vtable: ?*const VTable = null,

    pub const VTable = struct {
        report: *const fn (ctx: ?*anyopaque, id: u64, fraction: f32, note: []const u8) void,
        cancelled: *const fn (ctx: ?*anyopaque, id: u64) bool,
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
};

test "none is a no-op and reports no cancellation" {
    const p = Progress.none;
    p.report(0.5, "ignored"); // must not crash
    try std.testing.expect(!p.cancelled());
}
