const std = @import("std");
const editor = @import("editor");

pub fn printUsageGit() void {
    std.debug.print(
        \\Usage:  turian-cli mergedriver <base> <ours> <theirs> <output>
        \\
        \\Semantic three-way merge of scene/prefab JSON, matching objects by
        \\GUID instead of array position. Register it once per clone:
        \\  git config merge.turian-scene.driver "turian-cli mergedriver %O %A %B %A"
        \\
        \\Exits non-zero (conflict) if any object was changed the same way on
        \\both sides; <output> still receives a best-effort merged file.
        \\
    , .{});
}

pub fn cmdMergeDriver(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    const base_path = args.next() orelse return printUsageGit();
    const ours_path = args.next() orelse return printUsageGit();
    const theirs_path = args.next() orelse return printUsageGit();
    const output_path = args.next() orelse return printUsageGit();

    const base = try readFileAllowMissing(io, gpa, base_path);
    defer gpa.free(base);
    const ours = try readFileAllowMissing(io, gpa, ours_path);
    defer gpa.free(ours);
    const theirs = try readFileAllowMissing(io, gpa, theirs_path);
    defer gpa.free(theirs);

    var result = try editor.scene_merge.merge(gpa, base, ours, theirs);
    defer result.deinit(gpa);

    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = output_path, .data = result.json });

    if (result.conflicts.len == 0) {
        std.debug.print("[Turian] merged {s} cleanly -> {s}\n", .{ ours_path, output_path });
        return;
    }

    for (result.conflicts) |c| {
        std.debug.print("CONFLICT (scene): '{s}' ({s}): {s}\n", .{ c.name, c.guid, c.detail });
    }
    std.debug.print("[Turian] {d} conflict(s) written to {s} for manual resolution\n", .{ result.conflicts.len, output_path });
    return error.MergeConflict;
}

/// Treat a missing or empty file as "no objects" rather than failing the driver outright.
fn readFileAllowMissing(io: std.Io, gpa: std.mem.Allocator, path: []const u8) ![]u8 {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return gpa.dupe(u8, "");
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    return reader.interface.allocRemaining(gpa, .unlimited) catch gpa.dupe(u8, "");
}
