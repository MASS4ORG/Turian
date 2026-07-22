const std = @import("std");
const serde = @import("serde");
const Guid = @import("guid").Guid;

const AssetType = @import("../types/AssetType.zig").AssetType;
const ImportSettings = @import("../types/ImportSettings.zig");
const MetaFile = @import("../types/MetaFile.zig").MetaFile;
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

/// Ensure a .meta file exists for `asset_path` and is correctly classified.
/// Assigns a fresh GUID when missing (missing file, legacy format, or parse
/// failure) and reclassifies `asset_type`/`import_settings` whenever they're
/// still `.unknown` — even for a meta that already has a real GUID, which
/// happens when an earlier scan bug wrote a placeholder stub (real GUID,
/// `asset_type: "unknown"`) that was never healed since this used to only
/// fire on a nil GUID. Returns the up-to-date meta.
pub fn ensureMeta(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8) MetaFile {
    // Parse in a scratch arena so the meta's heap slices (manifests) don't leak;
    // the returned value carries only the scalar fields (see clearing below).
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var meta = readMeta(io, a, asset_path);
    var dirty = false;
    if (meta.guid.isNil()) {
        meta.guid = Guid.v4(io);
        dirty = true;
    }
    if (meta.asset_type == .unknown) {
        meta.asset_type = classifyByName(asset_path);
        dirty = true;
    }
    if (std.meta.activeTag(meta.import_settings) == .unknown and meta.asset_type != .unknown) {
        meta.import_settings = ImportSettings.defaultFor(meta.asset_type);
        dirty = true;
    }
    if (meta.source_hash == 0) {
        meta.source_hash = hashFile(io, a, asset_path);
        dirty = true;
    }
    if (dirty) writeMeta(io, a, asset_path, meta);

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
    // Pass 1: collect exact-case asset filenames in this directory (unbounded —
    // a fixed-size cap here previously truncated silently past 64 entries,
    // making Pass 2 below treat every asset past the cap as "orphaned" and
    // delete its otherwise-valid .meta file; a texture-heavy import (e.g.
    // hundreds of DDS files in one folder) hit this on every scan).
    // Used to detect and delete wrong-case .meta files (Windows tolerates
    // case mismatches; Linux does not, so we enforce correctness here).
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var names: std.ArrayList([]const u8) = .empty;
    {
        var it = dir.iterate();
        while (it.next(io) catch null) |e| {
            if (e.kind != .file or std.mem.endsWith(u8, e.name, ".meta")) continue;
            const owned = a.dupe(u8, e.name) catch continue;
            names.append(a, owned) catch continue;
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
            for (names.items) |n| {
                if (std.mem.eql(u8, stem, n)) {
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

// ── Tests ─────────────────────────────────────────────────────────────────────

test "scanAndEnsureMetas keeps every .meta in a directory with more than 64 assets" {
    // Regression test: scanDirForMetas' orphan/wrong-case cleanup pass used to
    // collect filenames into a fixed 64-entry array, silently dropping any past
    // the cap — so a texture-heavy directory (e.g. hundreds of DDS files, one
    // real project hit this with 633) had most of its valid .meta files deleted
    // and replaced with fresh GUIDs on every scan.
    const io = std.testing.io;
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets/Textures");

    const file_count = 70;
    var guids: [file_count]Guid = undefined;
    var name_buf: [file_count][32]u8 = undefined;
    var names: [file_count][]const u8 = undefined;
    for (0..file_count) |i| {
        const name = std.fmt.bufPrint(&name_buf[i], "tex_{d}.dds", .{i}) catch unreachable;
        names[i] = name;
        var path_buf: [64]u8 = undefined;
        const sub_path = std.fmt.bufPrint(&path_buf, "assets/Textures/{s}", .{name}) catch unreachable;
        try tmp.dir.writeFile(io, .{ .sub_path = sub_path, .data = "dds bytes" });
    }

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var assets_path_buf: [300]u8 = undefined;
    const assets_path = try std.fmt.bufPrint(&assets_path_buf, "{s}/assets", .{project_path});

    scanAndEnsureMetas(io, a, assets_path);
    for (0..file_count) |i| {
        var ap_buf: [300]u8 = undefined;
        const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/Textures/{s}", .{ assets_path, names[i] });
        const meta = readMeta(io, a, asset_path);
        try std.testing.expect(!meta.guid.isNil());
        guids[i] = meta.guid;
    }

    // Rescan (mirrors a second Studio/editor session opening the project) —
    // every GUID must be stable, not reassigned.
    scanAndEnsureMetas(io, a, assets_path);
    for (0..file_count) |i| {
        var ap_buf: [300]u8 = undefined;
        const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/Textures/{s}", .{ assets_path, names[i] });
        const meta = readMeta(io, a, asset_path);
        try std.testing.expect(!meta.guid.isNil());
        try std.testing.expect(meta.guid.eql(guids[i]));
    }
}

test {
    std.testing.refAllDecls(@This());
}
