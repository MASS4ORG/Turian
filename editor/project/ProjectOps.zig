const std = @import("std");
const engine = @import("engine");
const OpenResult = @import("../types/OpenResult.zig").OpenResult;
const ProjectConfig = @import("ProjectConfig.zig").ProjectConfig;

/// Sentinel file that marks a directory as a Turian project.
const PROJECT_FILE = "project.json";

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
    dir.createDirPath(io, "packages") catch {};

    // `project.json` is the source of truth; `build.zig.zon` is generated from
    // it (see ProjectConfig). Both are written from the same config so they
    // never drift, and the user never hand-edits the ZON.
    var fba_buf: [8192]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&fba_buf);
    const a = fba.allocator();
    if (ProjectConfig.initDefault(a, proj_name)) |cfg| {
        if (cfg.toJson(a)) |json| {
            dir.writeFile(io, .{ .sub_path = PROJECT_FILE, .data = json }) catch {};
        } else |_| {}
        if (cfg.toBuildZon(a, proj_name, "")) |zon| {
            dir.writeFile(io, .{ .sub_path = "build.zig.zon", .data = zon }) catch {};
        } else |_| {}
    } else |_| {}

    const settings = engine.ProjectSettings{ .project = .{ .name = proj_name } };
    var settings_buf: [4096]u8 = undefined;
    var settings_writer = std.Io.Writer.fixed(&settings_buf);
    if (settings.serialize(&settings_writer)) |_| {
        dir.writeFile(io, .{
            .sub_path = SETTINGS_SUBPATH,
            .data = settings_writer.buffered(),
        }) catch {};
    } else |_| {}

    dir.writeFile(io, .{ .sub_path = ".gitignore", .data = GITIGNORE_TEMPLATE }) catch {};
    dir.writeFile(io, .{ .sub_path = ".gitattributes", .data = GITATTRIBUTES_TEMPLATE }) catch {};
}

/// Build artifacts that don't belong in version control: the editor's
/// scratch build directory and the default build output folder
/// (`ProjectSettings.platform.build_output_path`).
const GITIGNORE_TEMPLATE =
    \\.cache/
    \\.public/
    \\
;

/// Attributes to route scene/prefab JSON through the GUID-based merge driver.
const GITATTRIBUTES_TEMPLATE =
    \\# Semantic merge for Turian scenes/prefabs (GUID-based, not line-based).
    \\# Register once per clone:
    \\#   git config merge.turian-scene.driver "turian-cli mergedriver %O %A %B %A"
    \\*.prefab merge=turian-scene
    \\
;
