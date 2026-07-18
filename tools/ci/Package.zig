/// `release package` — archive the assembled SDK from zig-out/ into .public/.
const std = @import("std");
const Proc = @import("Proc.zig");
const common = @import("Common.zig");

/// Package zig-out/sdk into a versioned platform archive under .public/.
pub fn run(io: std.Io, gpa: std.mem.Allocator, args: []const []const u8) !void {
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
    Proc.spawnAndWait(io, &.{ "rm", "-rf", sdk_versioned_path }) catch {};
    Proc.spawnAndWait(io, &.{ "mv", "zig-out/sdk", sdk_versioned_path }) catch |err| {
        std.debug.print("warning: could not rename zig-out/sdk → {s}: {any}\n", .{ sdk_versioned_path, err });
    };

    // SDK archive (primary artifact)
    switch (plt) {
        .linux_x86_64, .macos_x86_64, .macos_aarch64 => {
            const out = try std.fmt.allocPrint(gpa, ".public/{s}.tar.gz", .{sdk_versioned});
            defer gpa.free(out);
            try Proc.spawnAndWait(io, &.{ "tar", "-czf", out, "-C", "zig-out", sdk_versioned });
        },
        .windows_x86_64 => {
            // zip has no -C flag; run it from inside zig-out so the archive's
            // top-level entry is the versioned dir, not zig-out/turian-sdk-...
            const script = try std.fmt.allocPrint(gpa, "cd zig-out && zip -qr ../.public/{s}.zip {s}", .{ sdk_versioned, sdk_versioned });
            defer gpa.free(script);
            try Proc.spawnAndWait(io, &.{ "sh", "-c", script });
        },
    }
}
