/// Release tool — called by `zig build release-*` steps.
///
/// Subcommands (first argument, injected by build.zig):
///
///   check       --commits <file> [--initial] [--dry-run]
///   package     --platform <name> --version <v>
///   publish     --provider gitlab
///   verify      [--examples] [--sdk]
///   cut         --provider gitlab [--dry-run]
///   prune-cache [--days <n>] [--dir <path>]
///
/// Subcommand logic lives in tools/ci/; CI provider plugins in
/// tools/ci/ (see GitLab.zig).
const std = @import("std");
const Check = @import("Check.zig");
const Package = @import("Package.zig");
const Verify = @import("Verify.zig");
const Cut = @import("Cut.zig");
const PruneCache = @import("PruneCache.zig");
const gitlab = @import("GitLab.zig");

/// Main entry point for the release tool.
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
        try Check.run(io, gpa, Check.parseArgs(rest.items));
    } else if (std.mem.eql(u8, cmd, "package")) {
        try Package.run(io, gpa, rest.items);
    } else if (std.mem.eql(u8, cmd, "publish")) {
        try runPublish(io, gpa, rest.items, init.environ_map);
    } else if (std.mem.eql(u8, cmd, "verify")) {
        try Verify.run(io, gpa, init.environ_map, Verify.parseArgs(rest.items));
    } else if (std.mem.eql(u8, cmd, "cut")) {
        try Cut.run(io, gpa, init.environ_map, Cut.parseArgs(rest.items));
    } else if (std.mem.eql(u8, cmd, "prune-cache")) {
        try PruneCache.run(io, gpa, init.environ_map, PruneCache.parseArgs(rest.items));
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
        \\  check       --commits <file> [--initial] [--dry-run]
        \\  package     --platform <name> --version <v>
        \\  publish     --provider gitlab
        \\  verify      [--examples] [--sdk]
        \\  cut         --provider gitlab [--dry-run]
        \\  prune-cache [--days <n>] [--dir <path>]
    , .{});
}

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

test {
    std.testing.refAllDecls(@This());
}
