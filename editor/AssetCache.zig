const std = @import("std");
const Guid = @import("guid").Guid;
const AssetType = @import("types/AssetType.zig").AssetType;

// ── Extension map ─────────────────────────────────────────────────────────────

/// Runtime-format file extension for a cached artifact.
pub fn artifactExtension(asset_type: AssetType) []const u8 {
    return switch (asset_type) {
        .image => ".texture",
        .model => ".mesh",
        .audio => ".audioclip",
        .script => ".script",
        .scene => ".scene",
        .material => ".material",
        .data_asset => ".asset",
        .input_actions => ".inputactions",
        .unknown => ".bin",
    };
}

// ── Path helpers ──────────────────────────────────────────────────────────────

/// Fills `buf` with "{project_path}/.cache/assets".
pub fn cacheDir(project_path: []const u8, buf: []u8) ?[]u8 {
    return std.fmt.bufPrint(buf, "{s}/.cache/assets", .{project_path}) catch null;
}

/// Fills `buf` with the artifact path for `guid` and `asset_type`.
/// Pattern: {project_path}/.cache/assets/{guid}{ext}
pub fn artifactPath(
    project_path: []const u8,
    guid: Guid,
    asset_type: AssetType,
    buf: []u8,
) ?[]u8 {
    var guid_buf: [36]u8 = undefined;
    const guid_str = guid.toString(&guid_buf);
    return std.fmt.bufPrint(buf, "{s}/.cache/assets/{s}{s}", .{
        project_path, guid_str, artifactExtension(asset_type),
    }) catch null;
}

// ── Directory management ──────────────────────────────────────────────────────

/// Creates ".cache/assets/" under `project_path` if it does not already exist.
pub fn ensureDir(io: std.Io, project_path: []const u8) void {
    var buf: [1024]u8 = undefined;
    const dir_path = cacheDir(project_path, &buf) orelse return;
    std.Io.Dir.cwd().createDirPath(io, dir_path) catch {};
}

// ── Artifact existence ────────────────────────────────────────────────────────

/// Returns true if the cached artifact for `guid` exists on disk.
pub fn artifactExists(
    io: std.Io,
    project_path: []const u8,
    guid: Guid,
    asset_type: AssetType,
) bool {
    var buf: [1024]u8 = undefined;
    const path = artifactPath(project_path, guid, asset_type, &buf) orelse return false;
    var f = std.Io.Dir.cwd().openFile(io, path, .{}) catch return false;
    f.close(io);
    return true;
}

// ── Maintenance ───────────────────────────────────────────────────────────────

/// Deletes cache files whose stem GUID is not present in `valid_guids`.
pub fn purgeOrphaned(
    io: std.Io,
    project_path: []const u8,
    valid_guids: []const Guid,
) void {
    var cache_buf: [1024]u8 = undefined;
    const dir_path = cacheDir(project_path, &cache_buf) orelse return;

    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return;
    defer dir.close(io);

    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        if (entry.kind != .file) continue;
        const stem = std.fs.path.stem(entry.name);
        if (stem.len != 36) continue;
        const entry_guid = Guid.parse(stem) catch continue;
        const keep = for (valid_guids) |g| {
            if (g.eql(entry_guid)) break true;
        } else false;
        if (!keep) {
            var del_buf: [1024]u8 = undefined;
            const del_path = std.fmt.bufPrint(&del_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
            std.Io.Dir.cwd().deleteFile(io, del_path) catch {};
        }
    }
}

/// Deletes every file inside ".cache/assets/".
pub fn clearAll(io: std.Io, project_path: []const u8) void {
    var cache_buf: [1024]u8 = undefined;
    const dir_path = cacheDir(project_path, &cache_buf) orelse return;

    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return;
    defer dir.close(io);

    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        if (entry.kind != .file) continue;
        var del_buf: [1024]u8 = undefined;
        const del_path = std.fmt.bufPrint(&del_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
        std.Io.Dir.cwd().deleteFile(io, del_path) catch {};
    }
}
