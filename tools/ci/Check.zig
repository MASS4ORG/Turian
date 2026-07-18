/// `release check` — classify commits, bump the version in build.zig.zon,
/// update CHANGELOG.md, and write `.release-version` when a release is due.
const std = @import("std");
const Proc = @import("Proc.zig");
const conv = @import("Conventional.zig");
const changelog = @import("Changelog.zig");

pub const Options = struct {
    commits_path: ?[]const u8 = null,
    initial: bool = false,
    dry_run: bool = false,
};

/// Parse `check` CLI arguments.
pub fn parseArgs(args: []const []const u8) Options {
    var opts: Options = .{};
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--commits") and i + 1 < args.len) {
            opts.commits_path = args[i + 1];
            i += 1;
        } else if (std.mem.eql(u8, args[i], "--initial")) {
            opts.initial = true;
        } else if (std.mem.eql(u8, args[i], "--dry-run")) {
            opts.dry_run = true;
        }
    }
    return opts;
}

/// Run the check: reads commit subjects from `opts.commits_path`, computes the
/// bump, and (unless dry-run) rewrites build.zig.zon + CHANGELOG.md and writes
/// `.release-version`.
pub fn run(io: std.Io, gpa: std.mem.Allocator, opts: Options) !void {
    const current_version = blk: {
        const zon_content = Proc.readFile(io, gpa, "build.zig.zon") catch |err| {
            std.debug.print("error: cannot read build.zig.zon: {}\n", .{err});
            return err;
        };
        defer gpa.free(zon_content);
        const prefix = ".version = \"";
        const start = std.mem.indexOf(u8, zon_content, prefix) orelse return error.VersionNotFound;
        const end = std.mem.indexOfPos(u8, zon_content, start + prefix.len, "\"") orelse return error.VersionNotFound;
        break :blk try gpa.dupe(u8, zon_content[start + prefix.len .. end]);
    };
    defer gpa.free(current_version);

    var subjects: std.ArrayList([]const u8) = .empty;
    defer {
        for (subjects.items) |s| gpa.free(s);
        subjects.deinit(gpa);
    }

    if (opts.commits_path) |cp| {
        const raw = Proc.readFile(io, gpa, cp) catch |err| {
            std.debug.print("error: cannot read '{s}': {}\n", .{ cp, err });
            return err;
        };
        defer gpa.free(raw);
        var lines = std.mem.splitScalar(u8, raw, '\n');
        while (lines.next()) |line| {
            const t = std.mem.trim(u8, line, " \t\r");
            if (t.len == 0) continue;
            try subjects.append(gpa, try gpa.dupe(u8, t));
        }
    }

    for (subjects.items) |s| {
        if (!conv.isClassified(s)) {
            std.debug.print("[check] warning: unclassified commit: {s}\n", .{s});
        }
    }

    const new_version = blk: {
        if (opts.initial) {
            std.debug.print("[check] initial release → v{s}\n", .{current_version});
            break :blk try gpa.dupe(u8, current_version);
        }
        if (subjects.items.len == 0) {
            std.debug.print("[check] no commits — skipping.\n", .{});
            return;
        }
        const bump = conv.determineBump(subjects.items);
        if (bump == .none) {
            std.debug.print("[check] no releasable commits — skipping.\n", .{});
            return;
        }
        const v = try bumpVersion(gpa, current_version, bump);
        std.debug.print("[check] {s} → {s}  ({s})\n", .{ current_version, v, @tagName(bump) });
        break :blk v;
    };
    defer gpa.free(new_version);

    if (opts.dry_run) {
        std.debug.print("[check] dry-run: would release {s}\n", .{new_version});
        return;
    }

    if (!std.mem.eql(u8, new_version, current_version)) {
        const zon_content = try Proc.readFile(io, gpa, "build.zig.zon");
        defer gpa.free(zon_content);
        const new_zon = try replaceVersion(gpa, zon_content, current_version, new_version);
        defer gpa.free(new_zon);
        try Proc.writeCwd(io, "build.zig.zon", new_zon);
    }

    try changelog.update(io, gpa, new_version, subjects.items);
    try Proc.writeCwd(io, ".release-version", new_version);
    try std.Io.File.stdout().writeStreamingAll(io, new_version);
    try std.Io.File.stdout().writeStreamingAll(io, "\n");
}

/// Apply a semver bump to a "major.minor.patch[-pre]" string (pre-release
/// suffix is dropped).
pub fn bumpVersion(gpa: std.mem.Allocator, current: []const u8, bump: conv.BumpKind) ![]u8 {
    const hyphen = std.mem.indexOfScalar(u8, current, '-');
    const numeric = if (hyphen) |h| current[0..h] else current;

    var it = std.mem.splitScalar(u8, numeric, '.');
    var ma = try std.fmt.parseInt(u32, it.next() orelse "0", 10);
    var mi = try std.fmt.parseInt(u32, it.next() orelse "0", 10);
    var pa = try std.fmt.parseInt(u32, it.next() orelse "0", 10);

    switch (bump) {
        .major => {
            ma += 1;
            mi = 0;
            pa = 0;
        },
        .minor => {
            mi += 1;
            pa = 0;
        },
        .patch => {
            pa += 1;
        },
        .none => unreachable,
    }
    return std.fmt.allocPrint(gpa, "{d}.{d}.{d}", .{ ma, mi, pa });
}

fn replaceVersion(gpa: std.mem.Allocator, zon: []const u8, old: []const u8, new: []const u8) ![]const u8 {
    const old_str = try std.fmt.allocPrint(gpa, ".version = \"{s}\"", .{old});
    defer gpa.free(old_str);
    const new_str = try std.fmt.allocPrint(gpa, ".version = \"{s}\"", .{new});
    defer gpa.free(new_str);
    return std.mem.replaceOwned(u8, gpa, zon, old_str, new_str);
}

test "bumpVersion applies major, minor, and patch" {
    const gpa = std.testing.allocator;
    const major = try bumpVersion(gpa, "1.17.0", .major);
    defer gpa.free(major);
    try std.testing.expectEqualStrings("2.0.0", major);

    const minor = try bumpVersion(gpa, "1.17.3", .minor);
    defer gpa.free(minor);
    try std.testing.expectEqualStrings("1.18.0", minor);

    const patch = try bumpVersion(gpa, "1.17.3", .patch);
    defer gpa.free(patch);
    try std.testing.expectEqualStrings("1.17.4", patch);
}

test "bumpVersion drops pre-release suffixes" {
    const gpa = std.testing.allocator;
    const v = try bumpVersion(gpa, "2.0.0-rc1", .patch);
    defer gpa.free(v);
    try std.testing.expectEqualStrings("2.0.1", v);
}

test {
    std.testing.refAllDecls(@This());
}
