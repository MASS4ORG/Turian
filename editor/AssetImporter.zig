/// Asset import pipeline — validates and writes runtime artifacts to the cache.
/// For each asset, "import" currently copies the source bytes verbatim;
/// type-specific format conversion is deferred to future work.
const std = @import("std");
const Guid = @import("guid").Guid;
const AssetType = @import("types/AssetType.zig").AssetType;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");
const Progress = @import("Progress.zig").Progress;

const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;

// ── Importer versions ────────────────────────────────────────────────────────
// Bump the relevant constant to force a reimport of that asset class.

const VERSION_IMAGE: u32 = 1;
const VERSION_MODEL: u32 = 1;
const VERSION_AUDIO: u32 = 1;
const VERSION_OTHER: u32 = 1;

/// Current importer version for the given asset type.
pub fn importerVersion(asset_type: AssetType) u32 {
    return switch (asset_type) {
        .image => VERSION_IMAGE,
        .model => VERSION_MODEL,
        .audio => VERSION_AUDIO,
        else => VERSION_OTHER,
    };
}

// ── Single-asset import ───────────────────────────────────────────────────────

/// Import one asset into the cache if it needs updating.
/// No-ops when the artifact is up-to-date (same hash and importer version).
pub fn importAsset(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
) void {
    var meta = asset_meta.readMeta(io, allocator, asset_path);
    if (meta.guid.isNil()) return;

    const current_version = importerVersion(meta.asset_type);

    // Cheapest check first: if artifact exists and version matches, verify hash.
    if (asset_cache.artifactExists(io, project_path, meta.guid, meta.asset_type) and
        meta.importer_version == current_version)
    {
        if (asset_meta.hashFile(io, allocator, asset_path) == meta.source_hash) return;
    }

    writeArtifact(io, allocator, project_path, asset_path, &meta, current_version);
}

/// Force-reimport one asset, ignoring any existing cached artifact.
pub fn importAssetForce(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
) void {
    var meta = asset_meta.readMeta(io, allocator, asset_path);
    if (meta.guid.isNil()) return;
    writeArtifact(io, allocator, project_path, asset_path, &meta, importerVersion(meta.asset_type));
}

// ── Batch operations ──────────────────────────────────────────────────────────

/// Import every asset in `db` that needs updating, then remove orphaned
/// artifacts. Progress is reported per asset and cancellation is honoured
/// between assets (any artifacts already written are kept).
pub fn importAll(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    db: *AssetDatabase,
    progress: Progress,
) void {
    asset_cache.ensureDir(io, project_path);

    importEach(io, allocator, project_path, db, progress);

    collectAndPurge(io, allocator, project_path, db);
    progress.report(1, "Import complete");
}

/// Clear the entire cache then reimport every asset in `db`.
pub fn reimportAll(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    db: *AssetDatabase,
    progress: Progress,
) void {
    asset_cache.clearAll(io, project_path);
    asset_cache.ensureDir(io, project_path);

    importEach(io, allocator, project_path, db, progress);
    progress.report(1, "Reimport complete");
}

/// Import every asset in `db`, reporting progress and stopping early if the
/// caller requests cancellation.
fn importEach(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    db: *AssetDatabase,
    progress: Progress,
) void {
    const total = db.by_guid.count();
    var done: usize = 0;
    var it = db.by_guid.valueIterator();
    while (it.next()) |info| {
        if (progress.cancelled()) break;
        const frac: f32 = if (total == 0) 1 else @as(f32, @floatFromInt(done)) / @as(f32, @floatFromInt(total));
        progress.report(frac, std.fs.path.basename(info.path));
        importAsset(io, allocator, project_path, info.path);
        done += 1;
    }
}

// ── Internals ─────────────────────────────────────────────────────────────────

fn writeArtifact(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
    meta: *@import("types/MetaFile.zig").MetaFile,
    current_version: u32,
) void {
    var src = std.Io.Dir.cwd().openFile(io, asset_path, .{}) catch return;
    defer src.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = src.reader(io, &fbuf);
    const data = reader.interface.allocRemaining(allocator, .unlimited) catch return;
    defer allocator.free(data);

    var artifact_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, meta.guid, meta.asset_type, &artifact_buf) orelse return;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = art_path, .data = data }) catch return;

    meta.source_hash = std.hash.Fnv1a_64.hash(data);
    meta.importer_version = current_version;
    asset_meta.writeMeta(io, allocator, asset_path, meta.*);
}

fn collectAndPurge(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    db: *AssetDatabase,
) void {
    var guids: std.ArrayList(Guid) = .empty;
    defer guids.deinit(allocator);

    var it = db.by_guid.keyIterator();
    while (it.next()) |guid_ptr| guids.append(allocator, guid_ptr.*) catch {};

    asset_cache.purgeOrphaned(io, project_path, guids.items);
}
