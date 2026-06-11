/// Release tool — called by `zig build release-*` steps.
///
/// Subcommands (first argument, injected by build.zig):
///
///   check   --commits <file> [--initial] [--dry-run]
///   package --platform <name> --version <v>
///   publish --provider <name>
///
/// See tools/release/ci/GitLab.zig for the GitLab provider plugin.
const std = @import("std");
const common = @import("release/ci/Common.zig");
const gitlab = @import("release/ci/GitLab.zig");

const BumpKind = enum { none, patch, minor, major };

/// Main entry point for the release tool (check, package, publish).
pub fn main(init: std.process.Init) !void {
    const io = init.io;
    const gpa = init.gpa;

    var args_it = try std.process.Args.Iterator.initAllocator(init.minimal.args, gpa);
    defer args_it.deinit();
    _ = args_it.next(); // exe name

    const cmd = args_it.next() orelse {
        printHelp();
        return;
    };

    // Collect remaining args into a plain slice for subcommand handlers.
    var rest: std.ArrayList([]const u8) = .empty;
    defer rest.deinit(gpa);
    while (args_it.next()) |a| try rest.append(gpa, a);

    if (std.mem.eql(u8, cmd, "check")) {
        try runCheck(io, gpa, rest.items);
    } else if (std.mem.eql(u8, cmd, "package")) {
        try runPackage(io, gpa, rest.items);
    } else if (std.mem.eql(u8, cmd, "publish")) {
        try runPublish(io, gpa, rest.items, init.environ_map);
    } else {
        std.debug.print("Unknown subcommand: {s}\n", .{cmd});
        printHelp();
        return error.UnknownCommand;
    }
}

fn printHelp() void {
    std.debug.print(
        \\Turian release tool
        \\
        \\  check   --commits <file> [--initial] [--dry-run]
        \\  package --platform <name> --version <v>
        \\  publish --provider gitlab
    , .{});
}

// ── check ────────────────────────────────────────────────────────────────────

fn runCheck(io: std.Io, gpa: std.mem.Allocator, args: []const []const u8) !void {
    var commits_path: ?[]const u8 = null;
    var initial = false;
    var dry_run = false;

    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--commits") and i + 1 < args.len) {
            commits_path = args[i + 1];
            i += 1;
        } else if (std.mem.eql(u8, args[i], "--initial")) {
            initial = true;
        } else if (std.mem.eql(u8, args[i], "--dry-run")) {
            dry_run = true;
        }
    }

    const current_version = blk: {
        const zon_content = readFile(io, gpa, "build.zig.zon") catch |err| {
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

    if (commits_path) |cp| {
        const raw = readFile(io, gpa, cp) catch |err| {
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

    const new_version = blk: {
        if (initial) {
            std.debug.print("[check] initial release → v{s}\n", .{current_version});
            break :blk try gpa.dupe(u8, current_version);
        }
        if (subjects.items.len == 0) {
            std.debug.print("[check] no commits — skipping.\n", .{});
            return;
        }
        const bump = determineBump(subjects.items);
        if (bump == .none) {
            std.debug.print("[check] no releasable commits — skipping.\n", .{});
            return;
        }
        const v = try bumpVersion(gpa, current_version, bump);
        std.debug.print("[check] {s} → {s}  ({s})\n", .{ current_version, v, @tagName(bump) });
        break :blk v;
    };
    defer gpa.free(new_version);

    if (dry_run) {
        std.debug.print("[check] dry-run: would release {s}\n", .{new_version});
        return;
    }

    if (!std.mem.eql(u8, new_version, current_version)) {
        const zon_content = try readFile(io, gpa, "build.zig.zon");
        defer gpa.free(zon_content);
        const new_zon = try replaceVersion(gpa, zon_content, current_version, new_version);
        defer gpa.free(new_zon);
        try writeCwd(io, "build.zig.zon", new_zon);
    }

    try updateChangelog(io, gpa, new_version, subjects.items);
    try writeCwd(io, ".release-version", new_version);
    try std.Io.File.stdout().writeStreamingAll(io, new_version);
    try std.Io.File.stdout().writeStreamingAll(io, "\n");
}

// ── package ───────────────────────────────────────────────────────────────────

fn runPackage(io: std.Io, gpa: std.mem.Allocator, args: []const []const u8) !void {
    var platform_str: ?[]const u8 = null;
    var version_str: ?[]const u8 = null;

    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--platform") and i + 1 < args.len) {
            platform_str = args[i + 1];
            i += 1;
        } else if (std.mem.eql(u8, args[i], "--version") and i + 1 < args.len) {
            version_str = args[i + 1];
            i += 1;
        }
    }

    const plt_str = platform_str orelse return error.MissingPlatform;
    const plt = common.Platform.fromString(plt_str) orelse {
        std.debug.print("error: unknown platform '{s}'\n", .{plt_str});
        return error.UnknownPlatform;
    };

    var ver = version_str orelse return error.MissingVersion;
    if (ver.len > 0 and ver[0] == 'v') ver = ver[1..];

    std.Io.Dir.cwd().createDirPath(io, ".public") catch {};

    // Rename sdk/ staging dir to a versioned name so the archive extracts to a
    // clean top-level folder (e.g. turian-sdk-linux-x86_64-v1.0.0/).
    const sdk_versioned = try std.fmt.allocPrint(gpa, "turian-sdk-{s}-v{s}", .{ plt_str, ver });
    defer gpa.free(sdk_versioned);
    const sdk_versioned_path = try std.fmt.allocPrint(gpa, "zig-out/{s}", .{sdk_versioned});
    defer gpa.free(sdk_versioned_path);

    // Remove any stale rename target first — otherwise `mv` nests sdk/ *inside*
    // the existing dir (turian-sdk-.../sdk/), corrupting the archive layout.
    spawnAndWait(io, gpa, &.{ "rm", "-rf", sdk_versioned_path }) catch {};
    spawnAndWait(io, gpa, &.{ "mv", "zig-out/sdk", sdk_versioned_path }) catch |err| {
        std.debug.print("warning: could not rename zig-out/sdk → {s}: {any}\n", .{ sdk_versioned_path, err });
    };

    // SDK archive (primary artifact)
    switch (plt) {
        .linux_x86_64 => {
            const out = try std.fmt.allocPrint(gpa, ".public/{s}.tar.gz", .{sdk_versioned});
            defer gpa.free(out);
            try spawnAndWait(io, gpa, &.{ "tar", "-czf", out, "-C", "zig-out", sdk_versioned });
        },
        .windows_x86_64 => {
            // zip has no -C flag; run it from inside zig-out so the archive's
            // top-level entry is the versioned dir, not zig-out/turian-sdk-...
            const script = try std.fmt.allocPrint(gpa, "cd zig-out && zip -qr ../.public/{s}.zip {s}", .{ sdk_versioned, sdk_versioned });
            defer gpa.free(script);
            try spawnAndWait(io, gpa, &.{ "sh", "-c", script });
        },
        .macos_x86_64, .macos_aarch64 => {
            const out = try std.fmt.allocPrint(gpa, ".public/{s}.tar.gz", .{sdk_versioned});
            defer gpa.free(out);
            try spawnAndWait(io, gpa, &.{ "tar", "-czf", out, "-C", "zig-out", sdk_versioned });
        },
    }
}

// ── publish ───────────────────────────────────────────────────────────────────

fn runPublish(io: std.Io, gpa: std.mem.Allocator, args: []const []const u8, environ: *const std.process.Environ.Map) !void {
    var provider_str: ?[]const u8 = null;

    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--provider") and i + 1 < args.len) {
            provider_str = args[i + 1];
            i += 1;
        }
    }

    const provider = provider_str orelse return error.MissingProvider;
    if (std.mem.eql(u8, provider, "gitlab")) {
        try gitlab.run(io, gpa, environ);
    } else {
        std.debug.print("error: unknown provider '{s}'\n", .{provider});
        return error.UnknownProvider;
    }
}

// ── Versioning ────────────────────────────────────────────────────────────────

fn bumpVersion(gpa: std.mem.Allocator, current: []const u8, bump: BumpKind) ![]u8 {
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

// ── Conventional commit classification ───────────────────────────────────────

const Section = struct {
    label: []const u8,
    keywords: []const []const u8,
    bump: BumpKind,
};

const sections = [_]Section{
    .{ .label = "Features", .keywords = &.{"feat"}, .bump = .minor },
    .{ .label = "Bug Fixes", .keywords = &.{"fix"}, .bump = .patch },
    .{ .label = "Performance", .keywords = &.{"perf"}, .bump = .patch },
    .{ .label = "Other", .keywords = &.{ "chore", "ci", "docs", "refactor", "style", "test", "build" }, .bump = .patch },
};

fn determineBump(commits: []const []const u8) BumpKind {
    var result: BumpKind = .none;
    for (commits) |c| {
        if (isBreaking(c)) return .major;
        for (sections) |sec| {
            if (matchesKeywords(c, sec.keywords)) {
                if (sec.bump == .minor and result != .major) result = .minor;
                if (sec.bump == .patch and result == .none) result = .patch;
                break;
            }
        }
    }
    return result;
}

fn isBreaking(c: []const u8) bool {
    if (std.mem.indexOf(u8, c, "BREAKING CHANGE") != null) return true;
    const colon = std.mem.indexOf(u8, c, ":") orelse return false;
    return colon > 0 and c[colon - 1] == '!';
}

fn matchesKeywords(s: []const u8, keywords: []const []const u8) bool {
    for (keywords) |kw| {
        if (std.mem.startsWith(u8, s, kw)) {
            const rest = s[kw.len..];
            if (rest.len > 0 and (rest[0] == ':' or rest[0] == '(' or rest[0] == '!')) return true;
        }
    }
    return false;
}

// ── CHANGELOG update ──────────────────────────────────────────────────────────

fn updateChangelog(io: std.Io, gpa: std.mem.Allocator, version: []const u8, commits: []const []const u8) !void {
    var entry: std.ArrayList(u8) = .empty;
    defer entry.deinit(gpa);

    const date = currentDateStr(io);
    const header = try std.fmt.allocPrint(gpa, "## [{s}] - {s}\n", .{ version, &date });
    defer gpa.free(header);
    try entry.appendSlice(gpa, header);

    // 1. Breaking Changes
    {
        var items: std.ArrayList([]const u8) = .empty;
        defer items.deinit(gpa);
        for (commits) |c| {
            if (isBreaking(c)) try items.append(gpa, c);
        }
        if (items.items.len > 0) {
            try writeSection(gpa, &entry, "Breaking Changes", items.items);
        }
    }

    // 2. Regular sections
    for (sections) |sec| {
        var items: std.ArrayList([]const u8) = .empty;
        defer items.deinit(gpa);
        for (commits) |c| {
            if (matchesKeywords(c, sec.keywords)) try items.append(gpa, c);
        }
        if (items.items.len > 0) {
            try writeSection(gpa, &entry, sec.label, items.items);
        }
    }
    try entry.append(gpa, '\n');

    const existing = readFile(io, gpa, "CHANGELOG.md") catch "";
    defer if (existing.len > 0) gpa.free(existing);

    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(gpa);

    if (std.mem.startsWith(u8, existing, "# Changelog")) {
        const nl2 = std.mem.indexOf(u8, existing, "\n\n") orelse existing.len;
        try out.appendSlice(gpa, existing[0 .. nl2 + 2]);
        try out.appendSlice(gpa, entry.items);
        if (nl2 + 2 < existing.len) try out.appendSlice(gpa, existing[nl2 + 2 ..]);
    } else {
        try out.appendSlice(gpa, "# Changelog\n\n");
        try out.appendSlice(gpa, entry.items);
        if (existing.len > 0) try out.appendSlice(gpa, existing);
    }

    try writeCwd(io, "CHANGELOG.md", out.items);
}

fn writeSection(gpa: std.mem.Allocator, entry: *std.ArrayList(u8), label: []const u8, items: []const []const u8) !void {
    const sec_header = try std.fmt.allocPrint(gpa, "\n### {s}\n", .{label});
    defer gpa.free(sec_header);
    try entry.appendSlice(gpa, sec_header);
    for (items) |it| {
        const line = try std.fmt.allocPrint(gpa, "- {s}\n", .{it});
        defer gpa.free(line);
        try entry.appendSlice(gpa, line);
    }
}

fn currentDateStr(io: std.Io) [10]u8 {
    const now = std.Io.Clock.now(.real, io);
    const ts: u64 = @intCast(@divTrunc(now.nanoseconds, std.time.ns_per_s));
    const epoch_s = std.time.epoch.EpochSeconds{ .secs = ts };
    const yd = epoch_s.getEpochDay().calculateYearDay();
    const md = yd.calculateMonthDay();
    var buf: [10]u8 = undefined;
    _ = std.fmt.bufPrint(&buf, "{d:0>4}-{d:0>2}-{d:0>2}", .{
        yd.year, md.month.numeric(), md.day_index + 1,
    }) catch {};
    return buf;
}

// ── Shared helpers ────────────────────────────────────────────────────────────

fn readFile(io: std.Io, gpa: std.mem.Allocator, path: []const u8) ![]u8 {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    return reader.interface.allocRemaining(gpa, .unlimited);
}

fn writeCwd(io: std.Io, path: []const u8, data: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = data });
}

fn spawnAndWait(io: std.Io, _: std.mem.Allocator, argv: []const []const u8) !void {
    var child = try std.process.spawn(io, .{ .argv = argv });
    const term = try child.wait(io);
    switch (term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
}
