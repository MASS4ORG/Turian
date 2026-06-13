const std = @import("std");
const engine = @import("engine");
const OpenResult = @import("types/OpenResult.zig").OpenResult;

/// Sentinel file that marks a directory as a Turian project.
const PROJECT_FILE = "project.json";
/// JSON written to PROJECT_FILE for new projects.
const PROJECT_SENTINEL = "{\"turian_version\":\"0.16\"}\n";

/// Relative path (from project root) to the primary ProjectSettings asset.
const SETTINGS_SUBPATH = "assets/settings/project.projectsettings";

/// Open a project directory.  Returns valid=true when project.json is found;
/// the project name is hydrated from ProjectSettings when available.
pub fn openProject(io: std.Io, allocator: std.mem.Allocator, path: []const u8) OpenResult {
    var dir = std.Io.Dir.cwd().openDir(io, path, .{}) catch return .{};
    defer dir.close(io);

    // Validate: project.json must exist (content not used — it's just a sentinel).
    {
        var sentinel = dir.openFile(io, PROJECT_FILE, .{}) catch return .{};
        sentinel.close(io);
    }

    var result = OpenResult{ .valid = true };

    // Hydrate project name from ProjectSettings when present.
    var sf = dir.openFile(io, SETTINGS_SUBPATH, .{}) catch return result;
    defer sf.close(io);

    var fbuf: [8192]u8 = undefined;
    var reader = sf.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return result;
    defer allocator.free(content);

    const ps = engine.ProjectSettings.loadFromBytes(allocator, content) catch return result;
    defer ps.deinit(allocator);
    result.project = ps.toProject();

    return result;
}

/// Create a new project directory with the standard asset layout.
pub fn newProject(io: std.Io, path: []const u8, proj_name: []const u8) void {
    std.Io.Dir.cwd().createDirPath(io, path) catch {};

    var dir = std.Io.Dir.cwd().openDir(io, path, .{}) catch return;
    defer dir.close(io);

    dir.createDirPath(io, "assets/settings") catch {};
    dir.createDirPath(io, "scenes") catch {};

    dir.writeFile(io, .{ .sub_path = PROJECT_FILE, .data = PROJECT_SENTINEL }) catch {};

    const settings = engine.ProjectSettings{ .project = .{ .name = proj_name } };
    var settings_buf: [4096]u8 = undefined;
    var settings_writer = std.Io.Writer.fixed(&settings_buf);
    if (settings.serialize(&settings_writer)) |_| {
        dir.writeFile(io, .{
            .sub_path = SETTINGS_SUBPATH,
            .data = settings_writer.buffered(),
        }) catch {};
    } else |_| {}
}
