/// Model → materials + textures (one-to-many import). Generates:
///   * one `.material` per source material (metallic-roughness → built-in PBR),
///   * one `.texture` per *embedded* image (external images already have their
///     own source asset and are referenced by GUID, so they stay swappable).
/// Appends each as a `SubAsset` to the caller's manifest with a stable GUID
/// reused across reimports (`reuseOrNewGuid`, keyed against the manifest
/// snapshotted before this import). Best-effort: parse failures and
/// unsupported features are warned about, never fatal.
const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const AssetType = @import("../types/AssetType.zig").AssetType;
const MetaFile = @import("../types/MetaFile.zig").MetaFile;
const SubAsset = @import("../types/MetaFile.zig").SubAsset;
const ImportSettings = @import("../types/ImportSettings.zig").ImportSettings;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");

const log = std.log.scoped(.asset_importer);

const GltfLoader = engine.assets.GltfLoader;
const FbxLoader = engine.assets.FbxLoader;
const ModelInfo = engine.assets.ModelInfo;
const Material = engine.Material;

/// Generate material/image sub-assets for `asset_path` (glTF/GLB/FBX only —
/// a no-op for other model formats, e.g. OBJ). Appends new entries to `subs`;
/// GUIDs are reused by key from `prev` (the manifest before this import) so
/// reimport is idempotent. Respects the asset's "import materials" toggle.
pub fn generate(
    io: std.Io,
    allocator: std.mem.Allocator,
    project_path: []const u8,
    asset_path: []const u8,
    import_settings: ImportSettings,
    prev: []const SubAsset,
    subs: *std.ArrayList(SubAsset),
) void {
    switch (import_settings) {
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

    // `allocator` is the caller's import arena: everything allocated here —
    // including the sub-asset manifest entries appended to `subs` — lives
    // until the caller has written the meta, then is freed with the arena.
    const arena = allocator;

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
}

/// Reuse the GUID previously assigned to `key`, or mint a fresh one. Shared
/// with `ModelHierarchy.zig`'s `"mesh:{d}"`/`"hierarchy"` sub-assets, which
/// follow the same stable-GUID-by-key convention.
pub fn reuseOrNewGuid(prev: []const SubAsset, key: []const u8, io: std.Io) Guid {
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

/// Write raw bytes to the cache artifact for `guid`. Returns success. Shared
/// with `ModelHierarchy.zig` for its `"mesh:{d}"`/`"hierarchy"` artifacts.
pub fn writeBytesArtifact(
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
            .alpha_mask = m.alpha_mode == .mask,
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
