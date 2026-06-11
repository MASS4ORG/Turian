const std = @import("std");
const engine = @import("engine");
const OpenResult = @import("types/OpenResult.zig").OpenResult;

const PROJECT_FILE = "project.json";

/// Open a project directory and parse its project.json file.
pub fn openProject(io: std.Io, allocator: std.mem.Allocator, path: []const u8) OpenResult {
    var proj = engine.Project{};

    var dir = std.Io.Dir.cwd().openDir(io, path, .{}) catch return .{ .project = proj };
    defer dir.close(io);

    var file = dir.openFile(io, PROJECT_FILE, .{}) catch return .{ .project = proj };
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var file_reader = file.reader(io, &fbuf);
    const content = file_reader.interface.allocRemaining(allocator, .unlimited) catch return .{ .project = proj };
    defer allocator.free(content);

    if (parseProjectName(content)) |n| proj.setName(n);

    return .{ .project = proj };
}

/// Create a new project directory with default assets/scenes structure.
pub fn newProject(io: std.Io, path: []const u8, proj_name: []const u8) void {
    std.Io.Dir.cwd().createDirPath(io, path) catch {};

    var dir = std.Io.Dir.cwd().openDir(io, path, .{}) catch return;
    defer dir.close(io);

    dir.createDirPath(io, "assets") catch {};
    dir.createDirPath(io, "scenes") catch {};

    var json_buf: [512]u8 = undefined;
    const json = std.fmt.bufPrint(&json_buf, "{{\"name\":\"{s}\",\"version\":\"0.1.0\"}}\n", .{proj_name}) catch return;
    dir.writeFile(io, .{ .sub_path = PROJECT_FILE, .data = json }) catch {};
}

fn parseProjectName(json: []const u8) ?[]const u8 {
    const key = "\"name\":\"";
    const start_pos = std.mem.indexOf(u8, json, key) orelse return null;
    const value_start = start_pos + key.len;
    const end_pos = std.mem.indexOf(u8, json[value_start..], "\"") orelse return null;
    const value = json[value_start .. value_start + end_pos];
    return if (value.len > 0) value else null;
}
