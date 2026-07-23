/// Per-asset import staleness cache, stored under `.cache/` (gitignored) and
/// keyed by GUID — separate from the committed `.meta` so recomputable
/// bookkeeping (content hash, stat fast-path, importer version) never causes
/// a tracked-file diff.
const std = @import("std");
const serde = @import("serde");
const Guid = @import("guid").Guid;
const asset_cache = @import("AssetCache.zig");

/// Recomputable staleness signal for one asset's source file. Never authored
/// data — safe to drop or fall out of sync; the worst case is one extra
/// content hash on the next import.
pub const ImportStamp = struct {
    /// FNV-1a hash of the source file at last import.
    source_hash: u64 = 0,
    /// Size in bytes of the source file at last successful import.
    source_size: u64 = 0,
    /// Modification time (nanoseconds since epoch) of the source file at last
    /// successful import.
    source_mtime_ns: i96 = 0,
    /// Importer version that last cooked this asset; a bump forces reimport.
    importer_version: u32 = 0,
};

/// Fills `buf` with the stamp file path for `guid`.
/// Pattern: {project_path}/.cache/assets/{guid}.stamp
pub fn stampPath(project_path: []const u8, guid: Guid, buf: []u8) ?[]u8 {
    var guid_buf: [36]u8 = undefined;
    const guid_str = guid.toString(&guid_buf);
    return std.fmt.bufPrint(buf, "{s}/.cache/assets/{s}.stamp", .{ project_path, guid_str }) catch null;
}

/// Read the stamp for `guid`. Returns a zero-value default (which never
/// matches a real stat/hash, forcing a reimport) when missing or unreadable.
pub fn readStamp(io: std.Io, allocator: std.mem.Allocator, project_path: []const u8, guid: Guid) ImportStamp {
    var path_buf: [1024]u8 = undefined;
    const path = stampPath(project_path, guid, &path_buf) orelse return .{};

    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return .{};
    defer file.close(io);

    var fbuf: [256]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return .{};
    defer allocator.free(content);

    return serde.json.fromSlice(ImportStamp, allocator, content) catch .{};
}

/// Write `stamp` to the cache for `guid`. Never touches the committed `.meta`.
pub fn writeStamp(io: std.Io, allocator: std.mem.Allocator, project_path: []const u8, guid: Guid, stamp: ImportStamp) void {
    asset_cache.ensureDir(io, project_path);
    var path_buf: [1024]u8 = undefined;
    const path = stampPath(project_path, guid, &path_buf) orelse return;

    const json = serde.json.toSlice(allocator, stamp) catch return;
    defer allocator.free(json);

    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = json }) catch {};
}
