/// Asset packaging — bundles a project's cooked artifacts into a single `.oap`
/// (Open Asset Package) for the shipped game to consume.
///
/// The build pipeline is: source assets → cook (AssetImporter writes runtime
/// artifacts to `<project>/.cache/assets`) → **package** (this file reads those
/// artifacts and writes an `.oap`) → the game loads from the package via an
/// engine `OapProvider`, never touching the loose `assets/` folder.
const std = @import("std");
const oap = @import("open_asset_package");
const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;
const asset_cache = @import("AssetCache.zig");

/// Pack every cooked artifact referenced by `db` into an `.oap` at `oap_path`.
///
/// Each entry uses the asset GUID as its 128-bit id, the project-relative source
/// path as the virtual path (so the runtime can recover the file extension for
/// format dispatch), and the AssetType ordinal as the asset-type byte.
///
/// Returns the number of assets packaged.
pub fn packageProject(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    db: *AssetDatabase,
    oap_path: []const u8,
) !usize {
    var w = oap.Writer.init(gpa);
    defer w.deinit();

    var added: usize = 0;
    var it = db.by_guid.valueIterator();
    while (it.next()) |info| {
        var path_buf: [1024]u8 = undefined;
        const art_path = asset_cache.artifactPath(project_path, info.guid, info.asset_type, &path_buf) orelse continue;

        const bytes = std.Io.Dir.cwd().readFileAlloc(io, art_path, gpa, .unlimited) catch continue;
        defer gpa.free(bytes);

        w.add(.{
            .id = info.guid.bytes, // Guid is a struct { bytes: [16]u8 }
            .data = bytes,
            .vpath = projectRelative(project_path, info.path),
            .asset_type = @intFromEnum(info.asset_type),
        }) catch continue;
        added += 1;
    }

    var manifest: std.ArrayList(u8) = .empty;
    defer manifest.deinit(gpa);
    try manifest.print(
        gpa,
        "{{\"name\":\"{s}\",\"version\":\"1.0.0\",\"generator\":\"turian\",\"assets\":{d}}}",
        .{ std.fs.path.basename(project_path), added },
    );
    try w.setManifest(manifest.items);

    try w.writeToFile(io, oap_path);
    return added;
}

/// Strip a leading "<project_path>/" so paths in the package are relative to the
/// project root (where the running game sets its working directory).
fn projectRelative(project_path: []const u8, path: []const u8) []const u8 {
    if (path.len > project_path.len and
        std.mem.startsWith(u8, path, project_path) and
        (path[project_path.len] == '/' or path[project_path.len] == '\\'))
    {
        return path[project_path.len + 1 ..];
    }
    return path;
}

test "projectRelative strips the project prefix" {
    try std.testing.expectEqualStrings("assets/cube.obj", projectRelative("game", "game/assets/cube.obj"));
    try std.testing.expectEqualStrings("assets/cube.obj", projectRelative("game", "assets/cube.obj"));
}
