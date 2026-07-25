//! Project version-check and migration-runner primitives (ADR-0012). Compares
//! a project's recorded `turian_version` against the running engine version
//! and runs the registered migrations (`root.zig`) needed to catch it up.
const std = @import("std");

/// One version-bump migration. Registered by hand in `root.zig`.
pub const Migration = struct {
    /// Project version this migration brings a project up to.
    to_version: std.SemanticVersion,
    /// Shown in the pre-flight list before running.
    summary: []const u8,
    /// Non-empty when the user must also do something by hand; shown alongside `summary`.
    manual_steps: []const u8 = "",
    /// Advisory only — the runner does not skip or reorder on this (see `run`).
    idempotent: bool,
    run: *const fn (ctx: Context) anyerror!void,
};

pub const Context = struct {
    io: std.Io,
    allocator: std.mem.Allocator,
    /// Root directory of the project being migrated.
    project_path: []const u8,
};

/// Where a project's recorded version sits relative to the engine's, per
/// ADR-0012: major differences always trigger; minor differences trigger only
/// when `check_minor` is set; patch/prerelease differences never trigger.
pub const Direction = enum { up_to_date, older_minor, older_major, newer };

pub fn classify(
    project_version: std.SemanticVersion,
    engine_version: std.SemanticVersion,
    check_minor: bool,
) Direction {
    if (project_version.major != engine_version.major) {
        return if (project_version.major < engine_version.major) .older_major else .newer;
    }
    if (check_minor and project_version.minor != engine_version.minor) {
        return if (project_version.minor < engine_version.minor) .older_minor else .newer;
    }
    return .up_to_date;
}

/// Ascending slice of `all` strictly above `current` and up to `engine_version`
/// (inclusive). Assumes `all` is sorted ascending by `to_version` (enforced by
/// a test on the registry in `root.zig`).
pub fn pendingFor(
    all: []const Migration,
    current: std.SemanticVersion,
    engine_version: std.SemanticVersion,
) []const Migration {
    var start: usize = 0;
    while (start < all.len and all[start].to_version.order(current) != .gt) start += 1;
    var end: usize = start;
    while (end < all.len and all[end].to_version.order(engine_version) != .gt) end += 1;
    return all[start..end];
}

pub const RunResult = struct {
    ran: usize = 0,
    /// Index into the slice passed to `run`, or null if every migration succeeded.
    failed_at: ?usize = null,
    err: ?anyerror = null,
};

/// Runs `migrations` in order, stopping at the first failure. Does not touch
/// `project.json` — bump `turian_version` and save only when `failed_at == null`.
///
/// `current` is the project's version *before* this batch runs; it guards
/// against silently re-running a non-idempotent migration whose `to_version`
/// the project has already reached (a manually-edited `project.json` or an
/// interrupted prior run) rather than inferring anything from disk state.
pub fn run(migrations: []const Migration, current: std.SemanticVersion, ctx: Context) RunResult {
    var result: RunResult = .{};
    for (migrations, 0..) |m, i| {
        if (!m.idempotent and current.order(m.to_version) != .lt) {
            result.failed_at = i;
            result.err = error.MigrationAlreadyApplied;
            return result;
        }
        m.run(ctx) catch |err| {
            result.failed_at = i;
            result.err = err;
            return result;
        };
        result.ran += 1;
    }
    return result;
}

fn v(major: usize, minor: usize, patch: usize) std.SemanticVersion {
    return .{ .major = major, .minor = minor, .patch = patch };
}

test "classify: major difference always triggers regardless of check_minor" {
    try std.testing.expectEqual(Direction.older_major, classify(v(1, 0, 0), v(2, 0, 0), false));
    try std.testing.expectEqual(Direction.older_major, classify(v(1, 0, 0), v(2, 0, 0), true));
    try std.testing.expectEqual(Direction.newer, classify(v(3, 0, 0), v(2, 0, 0), false));
}

test "classify: minor difference triggers only when check_minor is set" {
    try std.testing.expectEqual(Direction.up_to_date, classify(v(2, 1, 0), v(2, 3, 0), false));
    try std.testing.expectEqual(Direction.older_minor, classify(v(2, 1, 0), v(2, 3, 0), true));
    try std.testing.expectEqual(Direction.newer, classify(v(2, 5, 0), v(2, 3, 0), true));
}

test "classify: patch and prerelease differences never trigger" {
    try std.testing.expectEqual(Direction.up_to_date, classify(v(2, 1, 0), v(2, 1, 9), true));
    try std.testing.expectEqual(Direction.up_to_date, classify(.{ .major = 2, .minor = 1, .patch = 0, .pre = "rc.1" }, v(2, 1, 0), true));
}

test "pendingFor: slices strictly-above-current up to engine_version, inclusive" {
    const migs = [_]Migration{
        .{ .to_version = v(1, 0, 0), .summary = "a", .idempotent = true, .run = testRun },
        .{ .to_version = v(2, 0, 0), .summary = "b", .idempotent = true, .run = testRun },
        .{ .to_version = v(3, 0, 0), .summary = "c", .idempotent = true, .run = testRun },
    };
    const pending = pendingFor(&migs, v(1, 0, 0), v(3, 0, 0));
    try std.testing.expectEqual(@as(usize, 2), pending.len);
    try std.testing.expectEqualStrings("b", pending[0].summary);
    try std.testing.expectEqualStrings("c", pending[1].summary);

    // Engine behind the last migration: only migrations up to engine_version run.
    const partial = pendingFor(&migs, v(0, 0, 0), v(2, 0, 0));
    try std.testing.expectEqual(@as(usize, 2), partial.len);
}

test "run: stops at first failure and reports index" {
    const migs = [_]Migration{
        .{ .to_version = v(1, 0, 0), .summary = "ok", .idempotent = true, .run = testRun },
        .{ .to_version = v(2, 0, 0), .summary = "fails", .idempotent = true, .run = testRunFails },
        .{ .to_version = v(3, 0, 0), .summary = "never runs", .idempotent = true, .run = testRun },
    };
    const result = run(&migs, v(0, 0, 0), testCtx());
    try std.testing.expectEqual(@as(usize, 1), result.ran);
    try std.testing.expectEqual(@as(?usize, 1), result.failed_at);
    try std.testing.expectEqual(error.Simulated, result.err.?);
}

test "run: refuses to re-run a non-idempotent migration already reached" {
    const migs = [_]Migration{
        .{ .to_version = v(1, 0, 0), .summary = "non-idempotent", .idempotent = false, .run = testRun },
    };
    const result = run(&migs, v(1, 0, 0), testCtx());
    try std.testing.expectEqual(@as(usize, 0), result.ran);
    try std.testing.expectEqual(@as(?usize, 0), result.failed_at);
    try std.testing.expectEqual(error.MigrationAlreadyApplied, result.err.?);
}

fn testCtx() Context {
    return .{ .io = undefined, .allocator = std.testing.allocator, .project_path = "" };
}

fn testRun(ctx: Context) anyerror!void {
    _ = ctx;
}

fn testRunFails(ctx: Context) anyerror!void {
    _ = ctx;
    return error.Simulated;
}
