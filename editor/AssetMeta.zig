const std = @import("std");
const serde = @import("serde");
const Guid = @import("guid").Guid;

const AssetType = @import("types/AssetType.zig").AssetType;
const ImportSettings = @import("types/ImportSettings.zig");
const MetaFile = @import("types/MetaFile.zig").MetaFile;
const asset_registry = @import("AssetRegistry.zig");

// ── Classification ────────────────────────────────────────────────────────────

/// Classify an asset type by file extension.
pub fn classifyByName(filename: []const u8) AssetType {
    return asset_registry.lookupByFilename(filename);
}

// ── Path helpers ──────────────────────────────────────────────────────────────

/// Returns the .meta file path for a given asset path, using a caller-supplied buffer.
pub fn metaPath(asset_path: []const u8, buf: []u8) ?[]u8 {
    return std.fmt.bufPrint(buf, "{s}.meta", .{asset_path}) catch null;
}

// ── Read / Write ──────────────────────────────────────────────────────────────

/// Read the .meta file for `asset_path`. Returns a default MetaFile on any error.
pub fn readMeta(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8) MetaFile {
    var path_buf: [1024]u8 = undefined;
    const path = metaPath(asset_path, &path_buf) orelse
        return .{ .asset_type = classifyByName(asset_path) };

    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch
        return .{ .asset_type = classifyByName(asset_path) };
    defer file.close(io);

    var fbuf: [512]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch
        return .{ .asset_type = classifyByName(asset_path) };
    defer allocator.free(content);

    return serde.json.fromSlice(MetaFile, allocator, content) catch
        .{ .asset_type = classifyByName(asset_path) };
}

/// Write `meta` as a pretty-printed JSON file at `<asset_path>.meta`.
pub fn writeMeta(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8, meta: MetaFile) void {
    var path_buf: [1024]u8 = undefined;
    const path = metaPath(asset_path, &path_buf) orelse return;

    const json = serde.json.toSliceWith(allocator, meta, .{ .pretty = true }) catch return;
    defer allocator.free(json);

    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = json }) catch {};
}

// ── Source file hashing ───────────────────────────────────────────────────────

/// Compute an FNV-1a hash of a file's contents for change detection.
/// Returns 0 on read error.
pub fn hashFile(io: std.Io, allocator: std.mem.Allocator, file_path: []const u8) u64 {
    var file = std.Io.Dir.cwd().openFile(io, file_path, .{}) catch return 0;
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return 0;
    defer allocator.free(content);
    return std.hash.Fnv1a_64.hash(content);
}

// ── Ensure / update ───────────────────────────────────────────────────────────

/// Ensure a .meta file exists for `asset_path`.
/// Creates or upgrades it with a fresh GUID when the existing meta has a nil GUID
/// (missing file, legacy format, or parse failure). Returns the up-to-date meta.
pub fn ensureMeta(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8) MetaFile {
    // Parse in a scratch arena so the meta's heap slices (manifests) don't leak;
    // the returned value carries only the scalar fields (see clearing below).
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var meta = readMeta(io, a, asset_path);
    if (meta.guid.isNil()) {
        meta.guid = Guid.v4(io);
        if (meta.asset_type == .unknown)
            meta.asset_type = classifyByName(asset_path);
        if (std.meta.activeTag(meta.import_settings) == .unknown and meta.asset_type != .unknown)
            meta.import_settings = ImportSettings.defaultFor(meta.asset_type);
        if (meta.source_hash == 0)
            meta.source_hash = hashFile(io, a, asset_path);

        writeMeta(io, a, asset_path, meta);
    }

    // The slice fields live in the arena and would dangle after return. Callers
    // of ensureMeta use only the GUID/type/version, so clear the manifests.
    meta.source_deps = &.{};
    meta.artifact_deps = &.{};
    meta.sub_assets = &.{};
    return meta;
}

/// Returns true when the asset has changed since its last recorded hash, the
/// importer version has been incremented, or no valid .meta exists yet.
pub fn needsReimport(
    io: std.Io,
    allocator: std.mem.Allocator,
    asset_path: []const u8,
    current_importer_version: u32,
) bool {
    const meta = readMeta(io, allocator, asset_path);
    if (meta.guid.isNil()) return true;
    if (meta.importer_version != current_importer_version) return true;
    return hashFile(io, allocator, asset_path) != meta.source_hash;
}

/// Stamp the source hash into the .meta after a successful import.
pub fn updateHash(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8) void {
    var meta = readMeta(io, allocator, asset_path);
    meta.source_hash = hashFile(io, allocator, asset_path);
    writeMeta(io, allocator, asset_path, meta);
}

// ── Directory scanning ────────────────────────────────────────────────────────

/// Recursively scan an assets directory and call `ensureMeta` for every
/// non-.meta file, guaranteeing every asset gets a stable GUID.
pub fn scanAndEnsureMetas(
    io: std.Io,
    allocator: std.mem.Allocator,
    assets_path: []const u8,
) void {
    var dir = std.Io.Dir.cwd().openDir(io, assets_path, .{ .iterate = true }) catch return;
    defer dir.close(io);
    scanDirForMetas(io, allocator, &dir, assets_path);
}

fn scanDirForMetas(
    io: std.Io,
    allocator: std.mem.Allocator,
    dir: *std.Io.Dir,
    dir_path: []const u8,
) void {
    // Pass 1: collect exact-case asset filenames in this directory.
    // Used to detect and delete wrong-case .meta files (Windows tolerates
    // case mismatches; Linux does not, so we enforce correctness here).
    const MAX_NAMES = 64;
    const NAME_LEN = 128;
    var names: [MAX_NAMES][NAME_LEN]u8 = undefined;
    var name_lens: [MAX_NAMES]u8 = .{0} ** MAX_NAMES;
    var name_count: usize = 0;
    {
        var it = dir.iterate();
        while (it.next(io) catch null) |e| {
            if (e.kind != .file or std.mem.endsWith(u8, e.name, ".meta")) continue;
            if (name_count >= MAX_NAMES) break;
            const l: u8 = @intCast(@min(e.name.len, NAME_LEN));
            @memcpy(names[name_count][0..l], e.name[0..l]);
            name_lens[name_count] = l;
            name_count += 1;
        }
    }

    // Pass 2: delete .meta files whose stem doesn't exactly match any asset name.
    // This catches both wrong-case meta files and orphaned meta files.
    {
        var it = dir.iterate();
        while (it.next(io) catch null) |e| {
            if (e.kind != .file or !std.mem.endsWith(u8, e.name, ".meta")) continue;
            const stem = e.name[0 .. e.name.len - ".meta".len];
            var exact = false;
            for (0..name_count) |i| {
                if (std.mem.eql(u8, stem, names[i][0..name_lens[i]])) {
                    exact = true;
                    break;
                }
            }
            if (!exact) {
                var bad_buf: [1024]u8 = undefined;
                const bad = std.fmt.bufPrint(&bad_buf, "{s}/{s}", .{ dir_path, e.name }) catch continue;
                std.Io.Dir.cwd().deleteFile(io, bad) catch {};
            }
        }
    }

    // Pass 3: recurse into subdirs and ensure metas for all assets.
    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        if (entry.kind == .directory) {
            var sub = dir.openDir(io, entry.name, .{ .iterate = true }) catch continue;
            defer sub.close(io);
            var sub_path_buf: [1024]u8 = undefined;
            const sub_path = std.fmt.bufPrint(&sub_path_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
            scanDirForMetas(io, allocator, &sub, sub_path);
        } else if (entry.kind == .file and !std.mem.endsWith(u8, entry.name, ".meta")) {
            var asset_path_buf: [1024]u8 = undefined;
            const asset_path = std.fmt.bufPrint(&asset_path_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
            _ = ensureMeta(io, allocator, asset_path);
        }
    }
}
