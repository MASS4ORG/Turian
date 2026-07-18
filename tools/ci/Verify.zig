/// `release verify` — post-build regression checks: build every example
/// project with the compiled CLI, and smoke-test the assembled SDK.
const std = @import("std");
const Proc = @import("Proc.zig");

pub const Options = struct {
    examples: bool = false,
    sdk: bool = false,
};

/// Parse `verify` CLI arguments; with no flags both checks run.
pub fn parseArgs(args: []const []const u8) Options {
    var opts: Options = .{};
    for (args) |a| {
        if (std.mem.eql(u8, a, "--examples")) opts.examples = true;
        if (std.mem.eql(u8, a, "--sdk")) opts.sdk = true;
    }
    if (!opts.examples and !opts.sdk) {
        opts.examples = true;
        opts.sdk = true;
    }
    return opts;
}

/// Run the selected verification passes.
pub fn run(io: std.Io, gpa: std.mem.Allocator, environ: *const std.process.Environ.Map, opts: Options) !void {
    if (opts.examples) try verifyExamples(io, gpa);
    if (opts.sdk) try verifySdk(io, gpa, environ);
}

/// Build every examples/* project with zig-out/bin/turian-cli.
fn verifyExamples(io: std.Io, gpa: std.mem.Allocator) !void {
    var dir = try std.Io.Dir.cwd().openDir(io, "examples", .{ .iterate = true });
    defer dir.close(io);
    var iter = dir.iterate();
    while (try iter.next(io)) |entry| {
        if (entry.kind != .directory) continue;
        const path = try std.fmt.allocPrint(gpa, "examples/{s}", .{entry.name});
        defer gpa.free(path);
        std.debug.print("==> {s}\n", .{path});
        Proc.spawnAndWait(io, &.{ "./zig-out/bin/turian-cli", "build", path }) catch |err| {
            std.debug.print("FAILED: {s}\n", .{path});
            return err;
        };
    }
}

/// Build a copy of examples/basic-project using only SDK-resident sources
/// (paths resolved by SdkLayout.zig, not baked paths) and check the outputs.
fn verifySdk(io: std.Io, gpa: std.mem.Allocator, environ: *const std.process.Environ.Map) !void {
    const tmp_base = environ.get("TMPDIR") orelse "/tmp";
    const proj = try std.fmt.allocPrint(gpa, "{s}/turian-sdk-smoke", .{tmp_base});
    defer gpa.free(proj);

    std.debug.print("==> SDK smoke-test ({s})\n", .{proj});
    std.Io.Dir.cwd().deleteTree(io, proj) catch {};
    try Proc.spawnAndWait(io, &.{ "cp", "-r", "examples/basic-project", proj });
    defer std.Io.Dir.cwd().deleteTree(io, proj) catch {};

    try Proc.spawnAndWait(io, &.{ "./zig-out/sdk/turian-cli", "build", proj });

    const game_bin = try std.fmt.allocPrint(gpa, "{s}/.cache/zig-out/bin/game", .{proj});
    defer gpa.free(game_bin);
    _ = std.Io.Dir.cwd().statFile(io, game_bin, .{}) catch {
        std.debug.print("game binary not found\n", .{});
        return error.VerifyFailed;
    };

    const oap = try std.fmt.allocPrint(gpa, "{s}/.cache/game.oap", .{proj});
    defer gpa.free(oap);
    _ = std.Io.Dir.cwd().statFile(io, oap, .{}) catch {
        std.debug.print("game.oap not found\n", .{});
        return error.VerifyFailed;
    };

    std.debug.print("SDK smoke-test PASSED\n", .{});
}
