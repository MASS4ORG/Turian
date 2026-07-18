/// `release prune-cache` — age out entries from Zig's global object cache.
/// Zig (0.16) never evicts cache entries itself, so a shared CI cache grows
/// without bound. Each o/<hash> entry is a self-contained content-addressed
/// unit; removing one only costs a recompile if it is ever needed again.
const std = @import("std");

pub const Options = struct {
    days: u32 = 14,
    dir: ?[]const u8 = null,
};

/// Parse `prune-cache` CLI arguments.
pub fn parseArgs(args: []const []const u8) Options {
    var opts: Options = .{};
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--days") and i + 1 < args.len) {
            opts.days = std.fmt.parseInt(u32, args[i + 1], 10) catch opts.days;
            i += 1;
        } else if (std.mem.eql(u8, args[i], "--dir") and i + 1 < args.len) {
            opts.dir = args[i + 1];
            i += 1;
        }
    }
    return opts;
}

/// Delete cache entries not modified within `days`. Missing cache dir is not
/// an error — CI jobs must not fail on a cold cache.
pub fn run(io: std.Io, gpa: std.mem.Allocator, environ: *const std.process.Environ.Map, opts: Options) !void {
    const dir_path = blk: {
        if (opts.dir) |d| break :blk try gpa.dupe(u8, d);
        const home = environ.get("HOME") orelse {
            std.debug.print("[prune-cache] HOME not set — skipping.\n", .{});
            return;
        };
        break :blk try std.fmt.allocPrint(gpa, "{s}/.cache/zig/o", .{home});
    };
    defer gpa.free(dir_path);

    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch {
        std.debug.print("[prune-cache] no cache at {s} — skipping.\n", .{dir_path});
        return;
    };
    defer dir.close(io);

    const now = std.Io.Clock.now(.real, io);
    const cutoff: i96 = now.nanoseconds - @as(i96, opts.days) * std.time.ns_per_day;

    var pruned: usize = 0;
    var iter = dir.iterate();
    while (try iter.next(io)) |entry| {
        if (entry.kind != .directory) continue;
        var sub = dir.openDir(io, entry.name, .{}) catch continue;
        const st = sub.stat(io) catch {
            sub.close(io);
            continue;
        };
        sub.close(io);
        if (st.mtime.nanoseconds >= cutoff) continue;
        dir.deleteTree(io, entry.name) catch continue;
        pruned += 1;
    }
    std.debug.print("[prune-cache] removed {d} entries older than {d} days from {s}\n", .{ pruned, opts.days, dir_path });
}
