//! Derive a model mesh's per-material-slot material table from its cooked
//! `material:{d}` sub-assets, and migrate pre-v2 scenes whose mesh renderers
//! still bind materials per submesh index. Shared by the Studio inspector,
//! model preview, and the `turian migrate` CLI pass.
const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");
const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;

const MAX = engine.MeshRendererComponent.MAX_MATERIALS;
const log = std.log.scoped(.scene_io);

/// Resolve the default material GUID for every material slot of a model mesh —
/// the material generated for each source material — writing them into `out`
/// indexed by slot (backed by `buf` for the GUID string bytes; unset slots are
/// empty slices). Returns the slot count (max `material_slot` + 1, clamped to
/// `MAX`), or 0 for non-model meshes, meshes with no cooked submesh table, or
/// models without generated materials.
pub fn slotMaterials(
    io: std.Io,
    alloc: std.mem.Allocator,
    db: *AssetDatabase,
    project_path: []const u8,
    mesh_guid_str: []const u8,
    buf: *[MAX][36]u8,
    out: *[MAX][]const u8,
) usize {
    if (mesh_guid_str.len == 0) return 0;
    const guid = Guid.parse(mesh_guid_str) catch return 0;
    const info = db.findByGuid(guid) orelse return 0;
    if (info.asset_type != .model) return 0;

    // Scratch arena for all temporaries: the meta's heap slices have no deinit,
    // and the cooked-mesh bytes/mesh are only read here. Resolved GUIDs are
    // copied into the caller's `buf`, so nothing escapes the arena.
    var arena = std.heap.ArenaAllocator.init(alloc);
    defer arena.deinit();
    const a = arena.allocator();

    const meta = asset_meta.readMeta(io, a, info.path);
    if (meta.sub_assets.len == 0) return 0;

    var art_buf: [1024]u8 = undefined;
    const art_path = asset_cache.artifactPath(project_path, guid, .model, &art_buf) orelse return 0;
    const bytes = std.Io.Dir.cwd().readFileAlloc(io, art_path, a, .unlimited) catch return 0;

    const mesh = engine.assets.Mesh.fromBytes(a, bytes) catch return 0;

    var max_slot: i32 = -1;
    for (mesh.submeshes) |sm| {
        if (sm.material_slot > max_slot) max_slot = sm.material_slot;
    }
    if (max_slot < 0) return 0;
    const n = @min(@as(usize, @intCast(max_slot + 1)), MAX);

    for (0..n) |slot| {
        out[slot] = &.{};
        var key_buf: [32]u8 = undefined;
        const key = std.fmt.bufPrint(&key_buf, "material:{d}", .{slot}) catch continue;
        for (meta.sub_assets) |s| {
            if (s.asset_type == .material and std.mem.eql(u8, s.key, key)) {
                out[slot] = s.guid.toString(&buf[slot]);
                break;
            }
        }
    }
    return n;
}

/// Rebuild each model mesh renderer's material table by slot, replacing the
/// pre-v2 per-submesh binding. Manual per-submesh overrides in old scenes are
/// reset to the model's slot defaults (the per-submesh format cannot express a
/// per-slot override); a warning is logged per migrated renderer. Returns the
/// number of mesh renderers migrated.
pub fn migrateSceneMaterials(
    io: std.Io,
    alloc: std.mem.Allocator,
    db: *AssetDatabase,
    project_path: []const u8,
    objects: []engine.SceneNode,
    count: usize,
) usize {
    var buf: [MAX][36]u8 = undefined;
    var out: [MAX][]const u8 = undefined;
    var migrated: usize = 0;

    for (objects[0..count]) |*obj| {
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const mr = &comp.mesh_renderer;
            const n = slotMaterials(io, alloc, db, project_path, mr.mesh.slice(), &buf, &out);
            if (n == 0) continue;

            mr.materials = .{engine.TypedAssetRef(.material){}} ** MAX;
            for (0..n) |slot| mr.materials[slot].set(out[slot]);
            mr.material_count = @intCast(n);
            migrated += 1;
            log.warn("migrated mesh_renderer materials to per-slot binding ({d} slots)", .{n});
        }
    }
    return migrated;
}
