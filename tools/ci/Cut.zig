/// `release cut` — full release orchestration: collect commits since the last
/// tag, bump the version + CHANGELOG (via Check), commit, tag, push, and
/// create the release through the CI provider.
const std = @import("std");
const Proc = @import("Proc.zig");
const Check = @import("Check.zig");
const gitlab = @import("GitLab.zig");

pub const Options = struct {
    provider: []const u8 = "gitlab",
    dry_run: bool = false,
};

/// Parse `cut` CLI arguments.
pub fn parseArgs(args: []const []const u8) Options {
    var opts: Options = .{};
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--provider") and i + 1 < args.len) {
            opts.provider = args[i + 1];
            i += 1;
        } else if (std.mem.eql(u8, args[i], "--dry-run")) {
            opts.dry_run = true;
        }
    }
    return opts;
}

/// Run the release flow. Git identity, authenticated remote, and tag pruning
/// only happen inside CI; a local run (use --dry-run) leaves the repo alone.
pub fn run(io: std.Io, gpa: std.mem.Allocator, environ: *const std.process.Environ.Map, opts: Options) !void {
    const in_ci = environ.get("GITLAB_CI") != null;

    if (in_ci and !opts.dry_run) {
        try Proc.spawnAndWait(io, &.{ "git", "config", "user.email", "ci@turian.engine" });
        try Proc.spawnAndWait(io, &.{ "git", "config", "user.name", "Turian CI" });
        if (try gitlab.pushUrl(gpa, environ)) |url| {
            defer gpa.free(url);
            try Proc.spawnAndWait(io, &.{ "git", "remote", "set-url", "origin", url });
        }
        // Persistent runner checkouts keep local-only tags from prior runs
        // that failed before their push; match the remote exactly so this job
        // is safe to retry.
        try Proc.spawnAndWait(io, &.{ "git", "fetch", "origin", "--tags", "--prune", "--prune-tags" });
    }

    // A stale .release-version from a previous run must not trigger a re-release.
    std.Io.Dir.cwd().deleteFile(io, ".release-version") catch {};

    const last: ?[]u8 = blk: {
        const out = Proc.runCapture(io, gpa, &.{ "git", "describe", "--tags", "--abbrev=0" }) catch break :blk null;
        defer gpa.free(out);
        const trimmed = std.mem.trim(u8, out, " \n\r\t");
        if (trimmed.len == 0) break :blk null;
        break :blk try gpa.dupe(u8, trimmed);
    };
    defer if (last) |l| gpa.free(l);

    const log_out = blk: {
        if (last) |l| {
            std.debug.print("[cut] last tag: {s}\n", .{l});
            const range = try std.fmt.allocPrint(gpa, "{s}..HEAD", .{l});
            defer gpa.free(range);
            break :blk try Proc.runCapture(io, gpa, &.{ "git", "log", "--format=%s", range });
        }
        std.debug.print("[cut] no prior tags — initial release.\n", .{});
        break :blk try Proc.runCapture(io, gpa, &.{ "git", "log", "--format=%s" });
    };
    defer gpa.free(log_out);
    try Proc.writeCwd(io, ".release-commits", log_out);

    try Check.run(io, gpa, .{
        .commits_path = ".release-commits",
        .initial = last == null,
        .dry_run = opts.dry_run,
    });
    if (opts.dry_run) {
        std.Io.Dir.cwd().deleteFile(io, ".release-commits") catch {};
        return;
    }

    const raw_version = Proc.readFile(io, gpa, ".release-version") catch {
        std.debug.print("[cut] nothing to release. Skipping.\n", .{});
        return;
    };
    defer gpa.free(raw_version);
    const version = std.mem.trim(u8, raw_version, " \n\r\t");
    const tag = try std.fmt.allocPrint(gpa, "v{s}", .{version});
    defer gpa.free(tag);
    std.debug.print("[cut] releasing {s}\n", .{tag});

    const msg = try std.fmt.allocPrint(gpa, "chore(release): {s}", .{tag});
    defer gpa.free(msg);
    try Proc.spawnAndWait(io, &.{ "git", "add", "build.zig.zon", "CHANGELOG.md" });
    try Proc.spawnAndWait(io, &.{ "git", "commit", "-m", msg });

    // Defense-in-depth on top of the prune above: drop any same-named local
    // tag left by an interrupted prior run before (re-)creating it.
    Proc.spawnAndWait(io, &.{ "git", "tag", "-d", tag }) catch {};
    const tag_msg = try std.fmt.allocPrint(gpa, "Release {s}", .{tag});
    defer gpa.free(tag_msg);
    try Proc.spawnAndWait(io, &.{ "git", "tag", "-a", tag, "-m", tag_msg });

    const ref = blk: {
        if (environ.get("CI_COMMIT_REF_NAME")) |r| break :blk try gpa.dupe(u8, r);
        const out = try Proc.runCapture(io, gpa, &.{ "git", "rev-parse", "--abbrev-ref", "HEAD" });
        defer gpa.free(out);
        break :blk try gpa.dupe(u8, std.mem.trim(u8, out, " \n\r\t"));
    };
    defer gpa.free(ref);
    const push_ref = try std.fmt.allocPrint(gpa, "HEAD:{s}", .{ref});
    defer gpa.free(push_ref);
    try Proc.spawnAndWait(io, &.{ "git", "push", "origin", push_ref, "--tags" });

    if (std.mem.eql(u8, opts.provider, "gitlab")) {
        try gitlab.run(io, gpa, environ);
    } else {
        std.debug.print("error: unknown provider '{s}'\n", .{opts.provider});
        return error.UnknownProvider;
    }
}
