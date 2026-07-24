/// Asset import pipeline — validates and writes runtime artifacts to the cache.
const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const AssetType = @import("../types/AssetType.zig").AssetType;
const MetaFile = @import("../types/MetaFile.zig").MetaFile;
const SubAsset = @import("../types/MetaFile.zig").SubAsset;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");
const asset_stamp = @import("AssetStamp.zig");
const ImportStamp = asset_stamp.ImportStamp;
const Progress = @import("../Progress.zig").Progress;

const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;

const log = std.log.scoped(.asset_importer);

// ── Importer versions ────────────────────────────────────────────────────────
// Bump the relevant constant to force a reimport of that asset class.

const VERSION_IMAGE: u32 = 3;
const VERSION_MODEL: u32 = 10;
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
    // One arena per import: the parsed meta and sub-asset manifest outlive the final meta write.
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var meta = asset_meta.readMeta(io, a, asset_path);
    if (meta.guid.isNil()) return;

    const current_version = importerVersion(meta.asset_type);
    var stamp = asset_stamp.readStamp(io, a, project_path, meta.guid);

    if (asset_cache.artifactExists(io, project_path, meta.guid, meta.asset_type) and
        stamp.importer_version == current_version)
    {
        // Size + mtime unchanged means content is unchanged; skip the full-content hash.
        if (asset_meta.statFile(io, asset_path)) |st| {
            if (st.size == stamp.source_size and st.mtime_ns == stamp.source_mtime_ns) return;
        }
        if (asset_meta.hashFile(io, a, asset_path) == stamp.source_hash) {
            // Hash matches despite stat mismatch; stamp current stat for next fast-path.
            if (asset_meta.statFile(io, asset_path)) |st| {
                stamp.source_size = st.size;
                stamp.source_mtime_ns = st.mtime_ns;
                asset_stamp.writeStamp(io, a, project_path, meta.guid, stamp);
            }
            return;
        }
    }

    writeArtifact(io, a, project_path, asset_path, &meta, current_version);
}

/// Force-reimport one asset, ignoring any existing cached artifact.
pub fn importAssetForce(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
) void {
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var meta = asset_meta.readMeta(io, a, asset_path);
    if (meta.guid.isNil()) return;
    writeArtifact(io, a, project_path, asset_path, &meta, importerVersion(meta.asset_type));
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

    // Surface generated sub-assets (materials/textures) so they are indexed,
    // resolvable, and — crucially — kept by the purge below.
    db.registerDerived(io, project_path);

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
    db.registerDerived(io, project_path);
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
    meta: *@import("../types/MetaFile.zig").MetaFile,
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

    // Models are cooked to the canonical binary mesh format; images bake in sRGB tagging.
    const artifact_bytes: []const u8 = if (meta.asset_type == .model)
        cookModelMesh(io, allocator, asset_path) orelse data
    else if (meta.asset_type == .image)
        cookImage(allocator, asset_path, data, meta.import_settings) orelse data
    else
        data;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = art_path, .data = artifact_bytes }) catch return;

    var stamp = ImportStamp{
        .source_hash = std.hash.Fnv1a_64.hash(data),
        .importer_version = current_version,
    };
    if (asset_meta.statFile(io, asset_path)) |st| {
        stamp.source_size = st.size;
        stamp.source_mtime_ns = st.mtime_ns;
    }
    asset_stamp.writeStamp(io, allocator, project_path, meta.guid, stamp);

    // A model can yield materials + textures as sub-assets; update meta before writing.
    if (meta.asset_type == .model)
        generateModelDerived(io, allocator, project_path, asset_path, meta);

    asset_meta.writeMeta(io, allocator, asset_path, meta.*);
}

// ── Model cooking ──────────────────────────────────────────────────────────────

/// Cook a source model (OBJ/glTF/GLB) into canonical binary mesh bytes. Returns
/// null on failure so the caller can fall back to copying the source verbatim.
/// Bytes are owned by `allocator` (the per-import arena).
fn cookModelMesh(io: std.Io, allocator: std.mem.Allocator, asset_path: []const u8) ?[]const u8 {
    var mesh = engine.assets.loadMesh(allocator, io, asset_path) catch return null;
    defer mesh.deinit();
    return mesh.encode(allocator) catch null;
}

// ── Image cooking ────────────────────────────────────────────────────────────

/// Cook an image per its `ImageImportSettings` color space (and, for DDS
/// normal maps, the DirectX-to-engine green-channel flip). Returns null on
/// parse failure or when no rewrite is needed, so the caller falls back to a
/// verbatim copy.
fn cookImage(allocator: std.mem.Allocator, asset_path: []const u8, data: []const u8, import_settings: @import("../types/ImportSettings.zig").ImportSettings) ?[]const u8 {
    const settings = switch (import_settings) {
        .image => |s| s,
        else => return null,
    };
    if (std.ascii.eqlIgnoreCase(std.fs.path.extension(asset_path), ".dds")) {
        return engine.assets.DdsLoader.cook(allocator, data, .{
            .srgb = settings.color_space == .srgb,
            .flip_green_channel = settings.texture_type == .normal_map and settings.flip_green_channel,
        }) catch null;
    }

    // Radiance HDR: cooked to a compact flat-RGBE envelope instead of copying the RLE source verbatim.
    if (std.ascii.eqlIgnoreCase(std.fs.path.extension(asset_path), ".hdr"))
        return engine.assets.HdrLoader.encodeEnvelope(allocator, data) catch null;

    // Non-DDS/KTX2: stb_image decodes to linear rgba8_unorm, so only sRGB needs a tag.
    if (settings.color_space != .srgb) return null;
    if (std.ascii.eqlIgnoreCase(std.fs.path.extension(asset_path), ".ktx2")) return null;
    return engine.assets.ImageLoader.wrapColorTag(allocator, data, true) catch null;
}

// ── Model → derived sub-assets (one-to-many) ────────────────────────────────
// Materials/images and per-mesh geometry + hierarchy share one manifest.

const model_derived_assets = @import("ModelDerivedAssets.zig");
const model_hierarchy = @import("ModelHierarchy.zig");

/// Generate derived sub-assets (materials, images, meshes, hierarchy) from a model source.
fn generateModelDerived(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
    meta: *MetaFile,
) void {
    // Snapshot the previous manifest for stable GUID reuse.
    const prev = meta.sub_assets;
    var subs: std.ArrayList(SubAsset) = .empty;

    model_derived_assets.generate(io, allocator, project_path, asset_path, meta.import_settings, prev, &subs);

    const ext = std.fs.path.extension(asset_path);
    if (std.ascii.eqlIgnoreCase(ext, ".gltf") or std.ascii.eqlIgnoreCase(ext, ".glb") or std.ascii.eqlIgnoreCase(ext, ".fbx"))
        model_hierarchy.generate(io, allocator, project_path, asset_path, prev, subs.items, &subs);

    // The arena outlives the caller's `writeMeta`.
    meta.sub_assets = subs.toOwnedSlice(allocator) catch &.{};
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

// ── Tests ─────────────────────────────────────────────────────────────────────

const Material = engine.Material;

test "importAsset skips content hashing when size and mtime are unchanged, but still detects real edits" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/data.bin", .data = "hello world" });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/data.bin", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAsset(io, a, project_path, asset_path);

    const meta1 = asset_meta.readMeta(io, a, asset_path);
    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, meta1.guid, meta1.asset_type, &art_buf).?;
    const bytes1 = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    try std.testing.expectEqualStrings("hello world", bytes1);
    const stamp1 = asset_stamp.readStamp(io, a, project_path, meta1.guid);

    // Rewrite with identical content: mtime changes (a fresh write), size
    // doesn't. The stat fast path may or may not trip depending on mtime
    // resolution, but either way the hash fallback agrees nothing changed —
    // the artifact must stay untouched and the recorded hash must be stable.
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/data.bin", .data = "hello world" });
    importAsset(io, a, project_path, asset_path);

    const stamp2 = asset_stamp.readStamp(io, a, project_path, meta1.guid);
    try std.testing.expectEqual(stamp1.source_hash, stamp2.source_hash);
    const bytes2 = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    try std.testing.expectEqualStrings("hello world", bytes2);

    // A genuine content edit (different size, so the fast path always misses)
    // must still trigger a reimport.
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/data.bin", .data = "goodbye, world!" });
    importAsset(io, a, project_path, asset_path);

    const stamp3 = asset_stamp.readStamp(io, a, project_path, meta1.guid);
    try std.testing.expect(stamp3.source_hash != stamp1.source_hash);
    const bytes3 = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    try std.testing.expectEqualStrings("goodbye, world!", bytes3);
}

test "reimporting an unchanged asset never rewrites the committed .meta bytes" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/data.bin", .data = "hello world" });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/data.bin", .{project_path});
    var meta_ap_buf: [300]u8 = undefined;
    const meta_path = try std.fmt.bufPrint(&meta_ap_buf, "{s}.meta", .{asset_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAsset(io, a, project_path, asset_path);
    const meta_bytes1 = try std.Io.Dir.cwd().readFileAlloc(io, meta_path, a, .unlimited);

    // Cache-only fields (hash/size/mtime/importer version) must never appear
    // in the committed .meta — they live in the gitignored stamp store instead.
    try std.testing.expect(std.mem.indexOf(u8, meta_bytes1, "source_hash") == null);
    try std.testing.expect(std.mem.indexOf(u8, meta_bytes1, "importer_version") == null);

    // Reimporting with no source change must not touch the committed .meta
    // at all, even byte-for-byte — this is what keeps `git status` clean.
    importAsset(io, a, project_path, asset_path);
    const meta_bytes2 = try std.Io.Dir.cwd().readFileAlloc(io, meta_path, a, .unlimited);
    try std.testing.expectEqualStrings(meta_bytes1, meta_bytes2);
}

test "model import generates a PBR material bound to its external texture" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");

    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "images": [{"uri": "tex.png"}],
        \\  "samplers": [{}],
        \\  "textures": [{"source": 0, "sampler": 0}],
        \\  "materials": [{
        \\    "name": "Mat",
        \\    "pbrMetallicRoughness": {
        \\      "baseColorFactor": [0.5, 0.25, 0.1, 1.0],
        \\      "metallicFactor": 0.3,
        \\      "roughnessFactor": 0.7,
        \\      "baseColorTexture": {"index": 0}
        \\    }
        \\  }]
        \\}
    ;
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.gltf", .data = gltf });
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/tex.png", .data = "not-a-real-png" });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.gltf", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    // The model meta should now list exactly one material sub-asset (the
    // external image is referenced by GUID, not turned into a sub-asset).
    const model_meta = asset_meta.readMeta(io, a, asset_path);
    var mat_guid: ?Guid = null;
    for (model_meta.sub_assets) |s| {
        if (s.asset_type == .material) mat_guid = s.guid;
        try std.testing.expect(s.asset_type != .image); // external → no sub-asset
    }
    try std.testing.expect(mat_guid != null);

    // The texture's own meta supplies the GUID the material must reference.
    var tex_ap_buf: [300]u8 = undefined;
    const tex_path = try std.fmt.bufPrint(&tex_ap_buf, "{s}/assets/tex.png", .{project_path});
    const tex_meta = asset_meta.readMeta(io, a, tex_path);
    try std.testing.expect(!tex_meta.guid.isNil());
    var tex_guid_buf: [36]u8 = undefined;
    const tex_guid_str = tex_meta.guid.toString(&tex_guid_buf);

    // Load the generated material artifact and verify the binding + factors.
    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, mat_guid.?, .material, &art_buf).?;
    const bytes = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    const mat = try Material.loadFromBytes(a, bytes);

    try std.testing.expectApproxEqAbs(@as(f32, 0.3), mat.scalar("metallic", 0), 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.7), mat.scalar("roughness", 0), 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), mat.vector("base_color", .{ 0, 0, 0, 0 })[0], 1e-5);
    try std.testing.expectEqualStrings(tex_guid_str, mat.texture("albedo_map"));
    try std.testing.expectEqualStrings("", mat.texture("normal_map"));
}

test "model import defaults a normal map's sibling image to linear color space" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");

    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "images": [{"uri": "base_color.png"}, {"uri": "normal.png"}],
        \\  "samplers": [{}],
        \\  "textures": [{"source": 0, "sampler": 0}, {"source": 1, "sampler": 0}],
        \\  "materials": [{
        \\    "name": "Mat",
        \\    "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}},
        \\    "normalTexture": {"index": 1}
        \\  }]
        \\}
    ;
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.gltf", .data = gltf });
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/base_color.png", .data = "not-a-real-png" });
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/normal.png", .data = "not-a-real-png" });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.gltf", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    var albedo_ap_buf: [300]u8 = undefined;
    const albedo_path = try std.fmt.bufPrint(&albedo_ap_buf, "{s}/assets/base_color.png", .{project_path});
    const albedo_meta = asset_meta.readMeta(io, a, albedo_path);
    try std.testing.expectEqual(@import("../types/ImportSettings.zig").ColorSpace.srgb, albedo_meta.import_settings.image.color_space);

    var normal_ap_buf: [300]u8 = undefined;
    const normal_path = try std.fmt.bufPrint(&normal_ap_buf, "{s}/assets/normal.png", .{project_path});
    const normal_meta = asset_meta.readMeta(io, a, normal_path);
    try std.testing.expectEqual(@import("../types/ImportSettings.zig").ColorSpace.linear, normal_meta.import_settings.image.color_space);
    try std.testing.expectEqual(@import("../types/ImportSettings.zig").TextureType.normal_map, normal_meta.import_settings.image.texture_type);
}

test "model import extracts an embedded image into a texture sub-asset" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");

    // Image embedded via a bufferView whose buffer is a base64 data URI
    // ("QUJDRA==" decodes to the 4 bytes "ABCD"). This is the .gltf spelling of
    // the GLB-embedded case cgltf surfaces through image->buffer_view.
    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "buffers": [{"uri": "data:application/octet-stream;base64,QUJDRA==", "byteLength": 4}],
        \\  "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 4}],
        \\  "images": [{"bufferView": 0, "mimeType": "image/png"}],
        \\  "textures": [{"source": 0}],
        \\  "materials": [{"name": "M", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}]
        \\}
    ;
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.gltf", .data = gltf });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.gltf", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    const model_meta = asset_meta.readMeta(io, a, asset_path);
    var img_guid: ?Guid = null;
    var mat_guid: ?Guid = null;
    for (model_meta.sub_assets) |s| switch (s.asset_type) {
        .image => img_guid = s.guid,
        .material => mat_guid = s.guid,
        else => {},
    };
    try std.testing.expect(img_guid != null);
    try std.testing.expect(mat_guid != null);

    // The extracted texture artifact holds the embedded bytes verbatim.
    var tex_buf: [1024]u8 = undefined;
    const tex_art = asset_cache.artifactPath(project_path, img_guid.?, .image, &tex_buf).?;
    const tex_bytes = try std.Io.Dir.cwd().readFileAlloc(io, tex_art, a, .unlimited);
    try std.testing.expectEqualStrings("ABCD", tex_bytes);

    // The material binds albedo to the extracted texture's GUID.
    var mat_buf: [1024]u8 = undefined;
    const mat_art = asset_cache.artifactPath(project_path, mat_guid.?, .material, &mat_buf).?;
    const mat_bytes = try std.Io.Dir.cwd().readFileAlloc(io, mat_art, a, .unlimited);
    const mat = try Material.loadFromBytes(a, mat_bytes);
    var ig_buf: [36]u8 = undefined;
    try std.testing.expectEqualStrings(img_guid.?.toString(&ig_buf), mat.texture("albedo_map"));
}

test "FBX model import generates a best-effort PBR material sub-asset" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.fbx", .data = engine.assets.FbxLoader.test_fbx });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.fbx", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    // Same dispatch path as glTF: exactly one material sub-asset, no image
    // (the fixture's Phong material has no texture reference).
    const model_meta = asset_meta.readMeta(io, a, asset_path);
    var mat_guid: ?Guid = null;
    for (model_meta.sub_assets) |s| {
        if (s.asset_type == .material) mat_guid = s.guid;
    }
    try std.testing.expect(mat_guid != null);

    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, mat_guid.?, .material, &art_buf).?;
    const bytes = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    const mat = try Material.loadFromBytes(a, bytes);

    // Classic Phong DiffuseColor maps to base_color; ufbx approximates
    // metallic/roughness from ShininessExponent (best-effort, no exact value
    // to assert beyond "produced a material at all").
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), mat.vector("base_color", .{ 0, 0, 0, 0 })[0], 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), mat.vector("base_color", .{ 0, 0, 0, 0 })[1], 1e-5);
}

test "glTF model import generates per-mesh and hierarchy sub-assets with stable GUIDs" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");

    // 3 vertices (VEC3 f32 positions) + 3 indices (u16), shared by both meshes.
    var bytes: [42]u8 = undefined;
    const positions = [_]f32{ 0, 0, 0, 1, 0, 0, 0, 1, 0 };
    @memcpy(bytes[0..36], std.mem.sliceAsBytes(&positions));
    const idx = [_]u16{ 0, 1, 2 };
    @memcpy(bytes[36..42], std.mem.sliceAsBytes(&idx));
    var b64buf: [64]u8 = undefined;
    const b64 = std.base64.standard.Encoder.encode(&b64buf, &bytes);

    const gltf = try std.fmt.allocPrint(a,
        \\{{
        \\  "asset": {{"version": "2.0"}},
        \\  "buffers": [{{"uri": "data:application/octet-stream;base64,{s}", "byteLength": 42}}],
        \\  "bufferViews": [
        \\    {{"buffer": 0, "byteOffset": 0, "byteLength": 36}},
        \\    {{"buffer": 0, "byteOffset": 36, "byteLength": 6}}
        \\  ],
        \\  "accessors": [
        \\    {{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"}},
        \\    {{"bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR"}}
        \\  ],
        \\  "meshes": [
        \\    {{"name": "MeshA", "primitives": [{{"attributes": {{"POSITION": 0}}, "indices": 1}}]}},
        \\    {{"name": "MeshB", "primitives": [{{"attributes": {{"POSITION": 0}}, "indices": 1}}]}}
        \\  ],
        \\  "nodes": [
        \\    {{"name": "Root", "children": [1, 2]}},
        \\    {{"name": "NodeA", "mesh": 0, "translation": [1, 0, 0]}},
        \\    {{"name": "NodeB", "mesh": 1, "translation": [2, 0, 0]}}
        \\  ],
        \\  "scenes": [{{"nodes": [0]}}],
        \\  "scene": 0
        \\}}
    , .{b64});
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.gltf", .data = gltf });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.gltf", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    const meta1 = asset_meta.readMeta(io, a, asset_path);
    var mesh0: ?Guid = null;
    var mesh1: ?Guid = null;
    var hierarchy: ?Guid = null;
    for (meta1.sub_assets) |s| {
        if (std.mem.eql(u8, s.key, "mesh:0")) mesh0 = s.guid;
        if (std.mem.eql(u8, s.key, "mesh:1")) mesh1 = s.guid;
        if (std.mem.eql(u8, s.key, "hierarchy")) hierarchy = s.guid;
    }
    try std.testing.expect(mesh0 != null);
    try std.testing.expect(mesh1 != null);
    try std.testing.expect(hierarchy != null);

    // The hierarchy sub-asset is a 3-node scene: Root + 2 mesh-bearing children.
    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, hierarchy.?, .scene, &art_buf).?;
    const scene_bytes = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    var nodes: [8]engine.SceneNode = undefined;
    var count: usize = 0;
    try std.testing.expect(@import("../project/SceneIo.zig").loadSceneFromBytes(a, scene_bytes, &nodes, &count));
    try std.testing.expectEqual(@as(usize, 3), count);

    var root_idx: ?usize = null;
    for (nodes[0..count], 0..) |*n, i| {
        if (std.mem.eql(u8, n.nameSlice(), "Root")) root_idx = i;
    }
    try std.testing.expect(root_idx != null);
    var child_count: usize = 0;
    for (nodes[0..count]) |*n| {
        if (n.parent == @as(i32, @intCast(root_idx.?))) {
            child_count += 1;
            try std.testing.expectEqual(@as(usize, 1), n.component_count);
            try std.testing.expect(n.components[0] == .mesh_renderer);
        }
    }
    try std.testing.expectEqual(@as(usize, 2), child_count);

    // Reimport is idempotent: same GUIDs reused by key.
    importAssetForce(io, a, project_path, asset_path);
    const meta2 = asset_meta.readMeta(io, a, asset_path);
    for (meta2.sub_assets) |s| {
        if (std.mem.eql(u8, s.key, "mesh:0")) try std.testing.expect(s.guid.eql(mesh0.?));
        if (std.mem.eql(u8, s.key, "hierarchy")) try std.testing.expect(s.guid.eql(hierarchy.?));
    }
}

test "FBX model import generates per-mesh and hierarchy sub-assets with stable GUIDs" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(io, "assets");

    // Two triangle meshes ("MeshA"/"MeshB") each with their own Model node,
    // both parented under an explicit "Group" null -- exercises hierarchy
    // grouping the same way the glTF test above does with its "Root".
    const fbx =
        \\; FBX 7.3.0 project file
        \\FBXHeaderExtension:  {
        \\  FBXHeaderVersion: 1003
        \\  FBXVersion: 7300
        \\}
        \\GlobalSettings:  {
        \\  Version: 1000
        \\  Properties70:  {
        \\    P: "UpAxis", "int", "Integer", "",1
        \\    P: "UpAxisSign", "int", "Integer", "",1
        \\    P: "FrontAxis", "int", "Integer", "",2
        \\    P: "FrontAxisSign", "int", "Integer", "",1
        \\    P: "CoordAxis", "int", "Integer", "",0
        \\    P: "CoordAxisSign", "int", "Integer", "",1
        \\    P: "OriginalUpAxis", "int", "Integer", "",1
        \\    P: "OriginalUpAxisSign", "int", "Integer", "",1
        \\    P: "UnitScaleFactor", "double", "Number", "",1
        \\  }
        \\}
        \\Objects:  {
        \\  Geometry: 1000000, "Geometry::MeshA", "Mesh" {
        \\    Vertices: *9 {
        \\      a: 0,0,0,1,0,0,0,1,0
        \\    }
        \\    PolygonVertexIndex: *3 {
        \\      a: 0,1,-3
        \\    }
        \\    GeometryVersion: 124
        \\    LayerElementNormal: 0 {
        \\      Version: 101
        \\      Name: ""
        \\      MappingInformationType: "ByPolygonVertex"
        \\      ReferenceInformationType: "Direct"
        \\      Normals: *9 {
        \\        a: 0,0,1,0,0,1,0,0,1
        \\      }
        \\    }
        \\    LayerElementUV: 0 {
        \\      Version: 101
        \\      Name: "UVMap"
        \\      MappingInformationType: "ByPolygonVertex"
        \\      ReferenceInformationType: "IndexToDirect"
        \\      UV: *6 {
        \\        a: 0,0,1,0,0,1
        \\      }
        \\      UVIndex: *3 {
        \\        a: 0,1,2
        \\      }
        \\    }
        \\    LayerElementMaterial: 0 {
        \\      Version: 101
        \\      Name: ""
        \\      MappingInformationType: "AllSame"
        \\      ReferenceInformationType: "IndexToDirect"
        \\      Materials: *1 {
        \\        a: 0
        \\      }
        \\    }
        \\    Layer: 0 {
        \\      Version: 100
        \\      LayerElement:  {
        \\        Type: "LayerElementNormal"
        \\        TypedIndex: 0
        \\      }
        \\      LayerElement:  {
        \\        Type: "LayerElementUV"
        \\        TypedIndex: 0
        \\      }
        \\      LayerElement:  {
        \\        Type: "LayerElementMaterial"
        \\        TypedIndex: 0
        \\      }
        \\    }
        \\  }
        \\  Geometry: 1000001, "Geometry::MeshB", "Mesh" {
        \\    Vertices: *9 {
        \\      a: 5,0,0,6,0,0,5,1,0
        \\    }
        \\    PolygonVertexIndex: *3 {
        \\      a: 0,1,-3
        \\    }
        \\    GeometryVersion: 124
        \\    LayerElementNormal: 0 {
        \\      Version: 101
        \\      Name: ""
        \\      MappingInformationType: "ByPolygonVertex"
        \\      ReferenceInformationType: "Direct"
        \\      Normals: *9 {
        \\        a: 0,0,1,0,0,1,0,0,1
        \\      }
        \\    }
        \\    LayerElementUV: 0 {
        \\      Version: 101
        \\      Name: "UVMap"
        \\      MappingInformationType: "ByPolygonVertex"
        \\      ReferenceInformationType: "IndexToDirect"
        \\      UV: *6 {
        \\        a: 0,0,1,0,0,1
        \\      }
        \\      UVIndex: *3 {
        \\        a: 0,1,2
        \\      }
        \\    }
        \\    LayerElementMaterial: 0 {
        \\      Version: 101
        \\      Name: ""
        \\      MappingInformationType: "AllSame"
        \\      ReferenceInformationType: "IndexToDirect"
        \\      Materials: *1 {
        \\        a: 0
        \\      }
        \\    }
        \\    Layer: 0 {
        \\      Version: 100
        \\      LayerElement:  {
        \\        Type: "LayerElementNormal"
        \\        TypedIndex: 0
        \\      }
        \\      LayerElement:  {
        \\        Type: "LayerElementUV"
        \\        TypedIndex: 0
        \\      }
        \\      LayerElement:  {
        \\        Type: "LayerElementMaterial"
        \\        TypedIndex: 0
        \\      }
        \\    }
        \\  }
        \\  Model: 2000000, "Model::Group", "Null" {
        \\    Version: 232
        \\    Properties70:  {
        \\    }
        \\  }
        \\  Model: 2000001, "Model::NodeA", "Mesh" {
        \\    Version: 232
        \\    Properties70:  {
        \\    }
        \\    Shading: T
        \\    Culling: "CullingOff"
        \\  }
        \\  Model: 2000002, "Model::NodeB", "Mesh" {
        \\    Version: 232
        \\    Properties70:  {
        \\    }
        \\    Shading: T
        \\    Culling: "CullingOff"
        \\  }
        \\  Material: 3000000, "Material::TestMat", "" {
        \\    Version: 102
        \\    ShadingModel: "Phong"
        \\    MultiLayer: 0
        \\    Properties70:  {
        \\      P: "DiffuseColor", "Color", "", "A",0.5,0.25,0.1
        \\      P: "SpecularColor", "Color", "", "A",0,0,0
        \\      P: "ShininessExponent", "Number", "", "A",10
        \\    }
        \\  }
        \\}
        \\Connections:  {
        \\  C: "OO",2000001,2000000
        \\  C: "OO",2000002,2000000
        \\  C: "OO",1000000,2000001
        \\  C: "OO",1000001,2000002
        \\  C: "OO",3000000,2000001
        \\  C: "OO",3000000,2000002
        \\}
        \\
    ;
    try tmp.dir.writeFile(io, .{ .sub_path = "assets/model.fbx", .data = fbx });

    var pp_buf: [256]u8 = undefined;
    const project_path = try std.fmt.bufPrint(&pp_buf, ".zig-cache/tmp/{s}", .{tmp.sub_path});
    var ap_buf: [300]u8 = undefined;
    const asset_path = try std.fmt.bufPrint(&ap_buf, "{s}/assets/model.fbx", .{project_path});

    asset_cache.ensureDir(io, project_path);
    _ = asset_meta.ensureMeta(io, a, asset_path);
    importAssetForce(io, a, project_path, asset_path);

    const meta1 = asset_meta.readMeta(io, a, asset_path);
    var mesh0: ?Guid = null;
    var mesh1: ?Guid = null;
    var hierarchy: ?Guid = null;
    for (meta1.sub_assets) |s| {
        if (std.mem.eql(u8, s.key, "mesh:0")) mesh0 = s.guid;
        if (std.mem.eql(u8, s.key, "mesh:1")) mesh1 = s.guid;
        if (std.mem.eql(u8, s.key, "hierarchy")) hierarchy = s.guid;
    }
    try std.testing.expect(mesh0 != null);
    try std.testing.expect(mesh1 != null);
    try std.testing.expect(hierarchy != null);

    // The hierarchy is: ufbx's synthetic root + "Group" + its 2 mesh-bearing
    // children -- 4 nodes total.
    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, hierarchy.?, .scene, &art_buf).?;
    const scene_bytes = try std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited);
    var nodes: [8]engine.SceneNode = undefined;
    var count: usize = 0;
    try std.testing.expect(@import("../project/SceneIo.zig").loadSceneFromBytes(a, scene_bytes, &nodes, &count));
    try std.testing.expectEqual(@as(usize, 4), count);

    var group_idx: ?usize = null;
    for (nodes[0..count], 0..) |*n, i| {
        if (std.mem.eql(u8, n.nameSlice(), "Group")) group_idx = i;
    }
    try std.testing.expect(group_idx != null);
    var child_count: usize = 0;
    for (nodes[0..count]) |*n| {
        if (n.parent == @as(i32, @intCast(group_idx.?))) {
            child_count += 1;
            try std.testing.expectEqual(@as(usize, 1), n.component_count);
            try std.testing.expect(n.components[0] == .mesh_renderer);
        }
    }
    try std.testing.expectEqual(@as(usize, 2), child_count);

    // Reimport is idempotent: same GUIDs reused by key.
    importAssetForce(io, a, project_path, asset_path);
    const meta2 = asset_meta.readMeta(io, a, asset_path);
    for (meta2.sub_assets) |s| {
        if (std.mem.eql(u8, s.key, "mesh:0")) try std.testing.expect(s.guid.eql(mesh0.?));
        if (std.mem.eql(u8, s.key, "hierarchy")) try std.testing.expect(s.guid.eql(hierarchy.?));
    }
}
