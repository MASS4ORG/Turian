/// Asset import pipeline — validates and writes runtime artifacts to the cache.
/// For each asset, "import" currently copies the source bytes verbatim;
/// type-specific format conversion is deferred to future work.
const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const AssetType = @import("../types/AssetType.zig").AssetType;
const MetaFile = @import("../types/MetaFile.zig").MetaFile;
const SubAsset = @import("../types/MetaFile.zig").SubAsset;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");
const Progress = @import("../Progress.zig").Progress;

const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;

const log = std.log.scoped(.asset_importer);

// ── Importer versions ────────────────────────────────────────────────────────
// Bump the relevant constant to force a reimport of that asset class.

// v2: DDS textures are cooked (sRGB tagging + optional normal-map green-channel
// flip) instead of copied verbatim; other image formats are still verbatim.
// v3: non-DDS/KTX2 images (PNG/JPEG/…) also cook in an sRGB tag when authored
// as color data, via a small envelope `ImageLoader` strips at load time.
const VERSION_IMAGE: u32 = 3;
// v2: generate materials + extract textures from glTF/GLB into sub-assets.
// v3: cook geometry into the canonical binary mesh format.
// v4: cook every mesh/primitive (submesh table) instead of only the first.
const VERSION_MODEL: u32 = 4;
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
    // One arena for the whole import of this asset: the parsed meta and any
    // generated sub-asset manifest live in it and must outlive `writeArtifact`'s
    // final meta write. Freed together here, so nothing leaks.
    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const a = arena.allocator();

    var meta = asset_meta.readMeta(io, a, asset_path);
    if (meta.guid.isNil()) return;

    const current_version = importerVersion(meta.asset_type);

    // Cheapest check first: if artifact exists and version matches, verify hash.
    if (asset_cache.artifactExists(io, project_path, meta.guid, meta.asset_type) and
        meta.importer_version == current_version)
    {
        if (asset_meta.hashFile(io, a, asset_path) == meta.source_hash) return;
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

    // Models are cooked into the canonical binary mesh format so the runtime
    // loads geometry with one fast loader (no OBJ/glTF parsing at run time).
    // Images are cooked to bake in sRGB tagging (and, for DDS normal maps, the
    // DirectX-to-engine green-channel flip). Other asset types are copied
    // verbatim for now.
    const artifact_bytes: []const u8 = if (meta.asset_type == .model)
        cookModelMesh(io, allocator, asset_path) orelse data
    else if (meta.asset_type == .image)
        cookImage(allocator, asset_path, data, meta.import_settings) orelse data
    else
        data;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = art_path, .data = artifact_bytes }) catch return;

    meta.source_hash = std.hash.Fnv1a_64.hash(data);
    meta.importer_version = current_version;

    // One-to-many: a model can yield materials + textures as sub-assets. This
    // updates meta.sub_assets, so run it before the meta is written.
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

    // Non-DDS/KTX2 (PNG/JPEG/…): stb_image always decodes to linear
    // `.rgba8_unorm`, which already matches a `.linear` setting, so only the
    // sRGB case needs a tag baked in. KTX2 already carries its own format
    // field and needs no cooking.
    if (settings.color_space != .srgb) return null;
    if (std.ascii.eqlIgnoreCase(std.fs.path.extension(asset_path), ".ktx2")) return null;
    return engine.assets.ImageLoader.wrapColorTag(allocator, data, true) catch null;
}

// ── Model → materials + textures (one-to-many) ────────────────────────────────

const GltfLoader = engine.assets.GltfLoader;
const FbxLoader = engine.assets.FbxLoader;
const ModelInfo = engine.assets.ModelInfo;
const Material = engine.Material;

/// Generate engine assets from a model's embedded materials and images:
///   * one `.material` per source material (metallic-roughness → built-in PBR),
///   * one `.texture` per *embedded* image (external images already have their
///     own source asset and are referenced by GUID, so they stay swappable).
/// Writes the artifacts to the cache and records each as a `SubAsset` in `meta`
/// with a stable GUID reused across reimports. Best-effort: parse failures and
/// unsupported features are warned about, never fatal.
fn generateModelDerived(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
    meta: *MetaFile,
) void {
    // Respect the "import materials" toggle.
    switch (meta.import_settings) {
        .model => |m| if (!m.import_materials) return,
        else => {},
    }

    const ext = std.fs.path.extension(asset_path);
    var info: ModelInfo = if (std.ascii.eqlIgnoreCase(ext, ".gltf") or std.ascii.eqlIgnoreCase(ext, ".glb"))
        GltfLoader.loadModelInfo(allocator, asset_path) catch return
    else if (std.ascii.eqlIgnoreCase(ext, ".fbx"))
        FbxLoader.loadModelInfo(allocator, asset_path) catch return
    else
        return; // e.g. .obj — no material/image data to extract.
    defer info.deinit();
    if (info.materials.len == 0) return;

    // `allocator` is the import arena (see `importAsset`): everything allocated
    // here — including the persisted sub-asset manifest — lives until the caller
    // has written the meta, then is freed with the arena.
    const arena = allocator;

    // Snapshot the previous manifest so GUIDs are reused by key across reimports.
    const prev = meta.sub_assets;

    var subs: std.ArrayList(SubAsset) = .empty;

    // Resolve every image to a referenceable texture GUID string (or null).
    const img_guids = arena.alloc(?[]const u8, info.images.len) catch return;
    @memset(img_guids, null);

    const image_roles = classifyImageRoles(arena, info.materials, info.images.len);

    for (info.images, 0..) |im, i| {
        var key_buf: [32]u8 = undefined;
        const key = std.fmt.bufPrint(&key_buf, "image:{d}", .{i}) catch continue;

        if (im.uri.len > 0) {
            // External file: it is (or becomes) a normal source asset. Bind by
            // its own GUID so the user can swap it later.
            const sibling = siblingPath(arena, asset_path, im.uri) orelse continue;
            const role = if (i < image_roles.len) image_roles[i] else .color;
            const sm = ensureImageMeta(io, arena, sibling, role);
            if (sm.guid.isNil()) {
                warnRel("[import] glTF references missing image", asset_path, im.uri);
                continue;
            }
            img_guids[i] = guidString(arena, sm.guid) catch null;
        } else if (im.data.len > 0) {
            // Embedded (GLB / data URI): extract into a cache-only texture asset.
            const guid = reuseOrNewGuid(prev, key, io);
            if (!writeBytesArtifact(io, project_path, guid, .image, im.data)) continue;
            img_guids[i] = guidString(arena, guid) catch null;
            const name = imageName(arena, im, i);
            subs.append(arena, .{ .guid = guid, .asset_type = .image, .key = arena.dupe(u8, key) catch key, .name = name }) catch {};
        } else {
            warnRel("[import] glTF image has no data (unsupported)", asset_path, im.name);
        }
    }

    // Generate one material asset per glTF material.
    for (info.materials, 0..) |m, i| {
        var key_buf: [32]u8 = undefined;
        const key = std.fmt.bufPrint(&key_buf, "material:{d}", .{i}) catch continue;
        const guid = reuseOrNewGuid(prev, key, io);

        if (!writeMaterialArtifact(io, project_path, guid, m, img_guids)) continue;

        const name = materialName(arena, m, i);
        subs.append(arena, .{ .guid = guid, .asset_type = .material, .key = arena.dupe(u8, key) catch key, .name = name }) catch {};
    }

    meta.sub_assets = subs.toOwnedSlice(arena) catch &.{};
    // meta.sub_assets points into `arena`; it is written by the caller's
    // writeMeta before this function returns, so the arena outlives that use.
}

/// Reuse the GUID previously assigned to `key`, or mint a fresh one.
fn reuseOrNewGuid(prev: []const SubAsset, key: []const u8, io: std.Io) Guid {
    for (prev) |s| {
        if (std.mem.eql(u8, s.key, key) and !s.guid.isNil()) return s.guid;
    }
    return Guid.v4(io);
}

/// Resolve a glTF image URI (relative to the model file) to a sibling path.
fn siblingPath(arena: std.mem.Allocator, model_path: []const u8, uri: []const u8) ?[]const u8 {
    const dir = std.fs.path.dirname(model_path) orelse ".";
    return std.fmt.allocPrint(arena, "{s}/{s}", .{ dir, uri }) catch null;
}

/// Texture role inferred from which material slots reference an image —
/// drives the default color space a newly-created sibling image asset gets.
const ImageRole = enum { color, normal, data };

/// Classify every image index by the role its materials use it in. Defaults
/// to `.color` (sRGB); a use as a normal/metallic-roughness/occlusion map
/// downgrades it to linear. `.normal` always wins over `.data` since the
/// green-channel-flip default (texture_type) only applies to normal maps.
fn classifyImageRoles(arena: std.mem.Allocator, materials: []const engine.assets.MaterialInfo, image_count: usize) []ImageRole {
    const roles = arena.alloc(ImageRole, image_count) catch return &.{};
    @memset(roles, .color);
    for (materials) |m| {
        if (m.normal.image_index) |idx| if (idx < roles.len) {
            roles[idx] = .normal;
        };
        if (m.metallic_roughness.image_index) |idx| if (idx < roles.len and roles[idx] == .color) {
            roles[idx] = .data;
        };
        if (m.occlusion.image_index) |idx| if (idx < roles.len and roles[idx] == .color) {
            roles[idx] = .data;
        };
    }
    return roles;
}

/// Like `asset_meta.ensureMeta`, but a brand-new meta for an image used only
/// as a data map (`role != .color`) defaults to a linear color space (and,
/// for normal maps, `texture_type = .normal_map`) instead of the general sRGB
/// default. Never touches an already-existing meta, so user edits always win.
fn ensureImageMeta(io: std.Io, arena: std.mem.Allocator, path: []const u8, role: ImageRole) MetaFile {
    const is_new = asset_meta.readMeta(io, arena, path).guid.isNil();
    const sm = asset_meta.ensureMeta(io, arena, path);
    if (!is_new or role == .color) return sm;

    var settings = switch (sm.import_settings) {
        .image => |s| s,
        else => return sm,
    };
    settings.color_space = .linear;
    if (role == .normal) settings.texture_type = .normal_map;

    var fixed = sm;
    fixed.import_settings = .{ .image = settings };
    asset_meta.writeMeta(io, arena, path, fixed);
    return fixed;
}

fn guidString(arena: std.mem.Allocator, guid: Guid) ![]const u8 {
    var buf: [36]u8 = undefined;
    return arena.dupe(u8, guid.toString(&buf));
}

fn imageName(arena: std.mem.Allocator, im: engine.assets.ImageInfo, index: usize) []const u8 {
    const ext = extForMime(im.mime_type);
    if (im.name.len > 0)
        return std.fmt.allocPrint(arena, "{s}{s}", .{ im.name, ext }) catch "image";
    return std.fmt.allocPrint(arena, "image_{d}{s}", .{ index, ext }) catch "image";
}

fn materialName(arena: std.mem.Allocator, m: engine.assets.MaterialInfo, index: usize) []const u8 {
    if (m.name.len > 0)
        return std.fmt.allocPrint(arena, "{s}.material", .{m.name}) catch "material.material";
    return std.fmt.allocPrint(arena, "material_{d}.material", .{index}) catch "material.material";
}

fn extForMime(mime: []const u8) []const u8 {
    if (std.mem.eql(u8, mime, "image/png")) return ".png";
    if (std.mem.eql(u8, mime, "image/jpeg")) return ".jpg";
    if (std.mem.eql(u8, mime, "image/webp")) return ".webp";
    return ".png";
}

/// Write raw bytes to the cache artifact for `guid`. Returns success.
fn writeBytesArtifact(
    io: std.Io,
    project_path: []const u8,
    guid: Guid,
    asset_type: AssetType,
    bytes: []const u8,
) bool {
    var buf: [1024]u8 = undefined;
    const path = asset_cache.artifactPath(project_path, guid, asset_type, &buf) orelse return false;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = bytes }) catch return false;
    return true;
}

/// Build a metallic-roughness PBR material from `m` and serialize it to the
/// cache artifact for `guid`. Texture slots are bound to resolved image GUIDs.
fn writeMaterialArtifact(
    io: std.Io,
    project_path: []const u8,
    guid: Guid,
    m: engine.assets.MaterialInfo,
    img_guids: []const ?[]const u8,
) bool {
    // Generate-if-missing: a material already in the cache may carry user edits
    // (e.g. a tweaked base color via the sub-asset inspector), so don't clobber
    // it. A full "Reimport All" clears the cache to regenerate from source.
    if (asset_cache.artifactExists(io, project_path, guid, .material)) return true;

    const slot = struct {
        fn texGuid(refs: []const ?[]const u8, r: engine.assets.TexRef) []const u8 {
            const idx = r.image_index orelse return "";
            if (idx >= refs.len) return "";
            return refs[idx] orelse "";
        }
    };

    var scalars = [_]Material.ScalarParam{
        .{ .name = "metallic", .value = m.metallic },
        .{ .name = "roughness", .value = m.roughness },
        .{ .name = "emissive_strength", .value = m.emissive_strength },
        .{ .name = "normal_scale", .value = m.normal_scale },
        .{ .name = "occlusion_strength", .value = m.occlusion_strength },
        .{ .name = "alpha_cutoff", .value = m.alpha_cutoff },
    };
    var vectors = [_]Material.VectorParam{
        .{ .name = "base_color", .value = m.base_color },
        .{ .name = "emissive", .value = .{ m.emissive[0], m.emissive[1], m.emissive[2], 1 } },
    };
    var textures = [_]Material.TextureParam{
        .{ .name = "albedo_map", .texture = slot.texGuid(img_guids, m.albedo) },
        .{ .name = "metallic_roughness_map", .texture = slot.texGuid(img_guids, m.metallic_roughness) },
        .{ .name = "normal_map", .texture = slot.texGuid(img_guids, m.normal) },
        .{ .name = "emissive_map", .texture = slot.texGuid(img_guids, m.emissive_map) },
        .{ .name = "occlusion_map", .texture = slot.texGuid(img_guids, m.occlusion) },
    };

    const mat = Material{
        .shader = engine.shader.pbr_guid,
        .scalars = &scalars,
        .vectors = &vectors,
        .textures = &textures,
        .render = .{
            .blend = if (m.alpha_mode == .blend) .alpha else .disabled,
            .cull = if (m.double_sided) .none else .back,
            .depth_write = m.alpha_mode != .blend,
            .depth_test = true,
        },
    };

    var buf: [16 * 1024]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    mat.serialize(&writer) catch return false;

    var path_buf: [1024]u8 = undefined;
    const path = asset_cache.artifactPath(project_path, guid, .material, &path_buf) orelse return false;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() }) catch return false;
    return true;
}

/// Log a warning using a project-relative path (never the user's full path).
fn warnRel(comptime msg: []const u8, model_path: []const u8, detail: []const u8) void {
    log.warn("{s}: {s} ({s})", .{ msg, std.fs.path.basename(model_path), detail });
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
