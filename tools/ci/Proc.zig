/// Shared subprocess and file helpers for the release tool.
const std = @import("std");

/// Run a command with inherited stdio; fails on non-zero exit.
pub fn spawnAndWait(io: std.Io, argv: []const []const u8) !void {
    var child = try std.process.spawn(io, .{ .argv = argv });
    const term = try child.wait(io);
    switch (term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
}

/// Run a command and return its raw stdout (caller owns); fails on non-zero exit.
pub fn runCapture(io: std.Io, gpa: std.mem.Allocator, argv: []const []const u8) ![]u8 {
    const res = try std.process.run(gpa, io, .{ .argv = argv });
    defer gpa.free(res.stderr);
    errdefer gpa.free(res.stdout);
    switch (res.term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
    return res.stdout;
}

/// Read an entire file from the current directory (caller owns).
pub fn readFile(io: std.Io, gpa: std.mem.Allocator, path: []const u8) ![]u8 {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    return reader.interface.allocRemaining(gpa, .unlimited);
}

/// Write a file in the current directory, replacing any existing content.
pub fn writeCwd(io: std.Io, path: []const u8, data: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = data });
}
