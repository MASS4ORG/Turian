//! Rebuilds `mesh_renderer.material_guids` for per-material-slot binding. Loading a scene already migrates the field in memory (`SceneIo.sceneCompToEngine`); this persists that migration to disk.
const std = @import("std");
const engine = @import("engine");
const MigrationApi = @import("MigrationApi.zig");
const AssetDatabase = @import("../../assets/AssetDatabase.zig").AssetDatabase;
const asset_meta = @import("../../assets/AssetMeta.zig");
const model_materials = @import("../../assets/ModelMaterials.zig");
const scene_io = @import("../SceneIo.zig");

pub const migration = MigrationApi.Migration{
    .to_version = .{ .major = 3, .minor = 0, .patch = 0 },
    .summary = "Rebuilds mesh_renderer material_guids for per-material-slot binding (converts single-field and per-submesh-indexed forms).",
    .idempotent = true,
    .run = run,
};

fn run(ctx: MigrationApi.Context) anyerror!void {
    const io = ctx.io;
    const gpa = ctx.allocator;
    const path = ctx.project_path;

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    asset_meta.scanAndEnsureMetas(io, gpa, assets);

    var db = AssetDatabase.init(gpa);
    defer db.deinit();
    db.scan(io, assets);

    // Heap-allocated (not `[MAX_OBJECTS]SceneNode` on the stack — that overflows
    // with the larger per-slot material table). Grown (doubling) per scene below
    // when `loadSceneFromBytes` reports a true node count past capacity (see its
    // doc comment) — a Bistro-scale FBX hierarchy scene can exceed the initial
    // `MAX_OBJECTS` default.
    var objects = try gpa.alloc(engine.SceneNode, engine.scene.MAX_OBJECTS);
    defer gpa.free(objects);
    const current_version = scene_io.CURRENT_VERSION;
    var scanned: usize = 0;
    var migrated: usize = 0;

    var it = db.enumerate(.scene);
    while (it.next()) |info| {
        scanned += 1;
        const bytes = std.Io.Dir.cwd().readFileAlloc(io, info.path, gpa, .unlimited) catch continue;
        defer gpa.free(bytes);

        // The scene `version` is the migration trigger: v1 (or the pre-version
        // default) needs rewriting; a current-version scene is already migrated.
        const version = scene_io.parseSceneVersion(bytes);
        if (version >= current_version) continue;

        var count: usize = 0;
        while (true) {
            if (!scene_io.loadSceneFromBytes(gpa, bytes, objects, &count)) break;
            if (count <= objects.len or objects.len >= engine.scene.GROWTH_CEILING) break;
            const new_cap = @min(count, engine.scene.GROWTH_CEILING);
            const grown = gpa.realloc(objects, new_cap) catch break;
            objects = grown;
        }
        if (count == 0 or count > objects.len) continue;
        // v1 bound materials per submesh; rebuild the per-slot table from the model.
        _ = model_materials.migrateSceneMaterials(io, gpa, &db, path, objects, count);
        scene_io.saveScene(io, info.path, objects, count, gpa);
        migrated += 1;
        std.debug.print("Migrated: {s}\n", .{info.path});
    }

    std.debug.print("Scanned {d} scene(s), migrated {d}.\n", .{ scanned, migrated });
}
