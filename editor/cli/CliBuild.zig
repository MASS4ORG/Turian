const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");
const scanner = editor.scanner;
const asset_meta = editor.asset_meta;
const GameBuild = editor.GameBuild;
const project_ops = editor.project_ops;
const build_options = @import("turian_build_options");

/// Task progress wrapper that echoes to stdout.
const CliTask = struct {
    tm: *editor.TaskManager,
    id: u64,
    last_pct: i32 = -1,

    fn start(tm: *editor.TaskManager, kind: editor.TaskKind, label: []const u8) CliTask {
        return .{ .tm = tm, .id = tm.begin(kind, label) };
    }

    fn report(ctx: ?*anyopaque, id: u64, fraction: f32, note: []const u8) void {
        const self: *CliTask = @ptrCast(@alignCast(ctx.?));
        self.tm.setProgress(id, fraction, note);
        const pct: i32 = @intFromFloat(fraction * 100);
        if (pct == self.last_pct) return;
        self.last_pct = pct;
        std.debug.print("[{d:>3}%] {s}\n", .{ @as(u32, @intCast(pct)), note });
    }

    fn cancelled(ctx: ?*anyopaque, id: u64) bool {
        const self: *CliTask = @ptrCast(@alignCast(ctx.?));
        return self.tm.isCancelRequested(id);
    }

    const vtable = editor.Progress.VTable{ .report = report, .cancelled = cancelled };

    fn progress(self: *CliTask) editor.Progress {
        return .{ .ctx = self, .id = self.id, .vtable = &vtable };
    }

    fn printStatus(self: *CliTask) void {
        if (self.tm.get(self.id)) |t| {
            std.debug.print("{s}: {s}\n", .{ t.kind.text(), t.status.text() });
        }
    }
};

pub fn cmdNewProject(io: std.Io, path: []const u8, proj_name: []const u8) !void {
    project_ops.newProject(io, path, proj_name);
    std.debug.print("[Turian] Project '{s}' created at: {s}\n", .{ proj_name, path });
}

pub fn cmdInfo(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !void {
    const result = project_ops.openProject(io, gpa, path);
    if (!result.valid) {
        std.debug.print("Not a Turian project (no project.json): {s}\n", .{path});
        return error.ProjectNotFound;
    }
    const p = result.project;
    if (p.nameSlice().len > 0) {
        std.debug.print("Project: {s}  v{d}.{d}.{d}\n", .{
            p.nameSlice(), p.major, p.minor, p.patch,
        });
    }

    var components: [scanner.MAX_COMPONENTS]scanner.ComponentDef = undefined;
    var count: usize = 0;
    scanner.populateBuiltins(&components, &count);

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    scanner.scanAssetsDir(io, gpa, assets, &components, &count);
    asset_meta.scanAndEnsureMetas(io, gpa, assets);

    const n_builtin = builtinCount(components[0..count]);
    const n_user = count - n_builtin;
    std.debug.print("Components: {d} builtin", .{n_builtin});
    if (n_user > 0) std.debug.print(", {d} user scripts", .{n_user});
    std.debug.print("\n", .{});
}

fn bakedBuildConfig() GameBuild.BuildConfig {
    return .{
        .engine_root = build_options.engine_root_path,
        .editor_root = build_options.editor_root_path,
        .cgltf_wrap_c = build_options.cgltf_wrap_c_path,
        .vendor_include = build_options.vendor_include_path,
        .build_root = build_options.build_root_path,
        .sdl3_lib = build_options.sdl3_lib_path,
        .math_root = build_options.math_root_path,
        .guid_root = build_options.guid_root_path,
        .oap_root = build_options.oap_root_path,
        .serde_root = build_options.serde_root_path,
        .serde_compat_root = build_options.serde_compat_root_path,
        .ktx2_root = build_options.ktx2_root_path,
        .gpu_root = build_options.gpu_root_path,
        .gpu_sdl3_c = build_options.gpu_sdl3_c_path,
        .render_root = build_options.render_root_path,
        .sdl3_include = build_options.sdl3_include_path,
        .ui_render_root = build_options.ui_render_root_path,
        .dvui_url = build_options.dvui_url,
        .dvui_hash = build_options.dvui_hash,
        .engine_version = build_options.version,
    };
}

pub fn cmdBuild(io: std.Io, gpa: std.mem.Allocator, path: []const u8, environ: *const std.process.Environ.Map) !void {
    const baked = bakedBuildConfig();
    var cfg_arena = std.heap.ArenaAllocator.init(gpa);
    defer cfg_arena.deinit();
    const config = editor.sdk_layout.resolveBuildConfig(io, cfg_arena.allocator(), environ, baked);

    var components: [scanner.MAX_COMPONENTS]scanner.ComponentDef = undefined;
    var count: usize = 0;
    scanner.populateBuiltins(&components, &count);

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    scanner.scanAssetsDir(io, gpa, assets, &components, &count);
    asset_meta.scanAndEnsureMetas(io, gpa, assets);

    var tm = editor.TaskManager.init();
    var task = CliTask.start(&tm, .build, "Build game");
    const ok = GameBuild.buildGame(io, path, &components, count, config, task.progress());
    if (ok) tm.complete(task.id) else tm.fail(task.id, "build failed");
    task.printStatus();
    if (!ok) return error.BuildFailed;
    std.debug.print("[Turian] Build complete.\n", .{});
}

pub fn cmdPlayBuild(io: std.Io, gpa: std.mem.Allocator, path: []const u8, environ: *const std.process.Environ.Map) !void {
    const baked = GameBuild.BuildConfig{
        .engine_root = build_options.engine_root_path,
        .editor_root = build_options.editor_root_path,
        .cgltf_wrap_c = build_options.cgltf_wrap_c_path,
        .vendor_include = build_options.vendor_include_path,
        .build_root = build_options.build_root_path,
        .sdl3_lib = build_options.sdl3_lib_path,
        .math_root = build_options.math_root_path,
        .guid_root = build_options.guid_root_path,
        .oap_root = build_options.oap_root_path,
        .serde_root = build_options.serde_root_path,
        .serde_compat_root = build_options.serde_compat_root_path,
        .ktx2_root = build_options.ktx2_root_path,
        .gpu_root = build_options.gpu_root_path,
        .gpu_sdl3_c = build_options.gpu_sdl3_c_path,
        .render_root = build_options.render_root_path,
        .sdl3_include = build_options.sdl3_include_path,
    };
    var cfg_arena = std.heap.ArenaAllocator.init(gpa);
    defer cfg_arena.deinit();
    const a = cfg_arena.allocator();
    const config = editor.sdk_layout.resolveBuildConfig(io, a, environ, baked);

    var components: [scanner.MAX_COMPONENTS]scanner.ComponentDef = undefined;
    var count: usize = 0;
    scanner.populateBuiltins(&components, &count);

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    scanner.scanAssetsDir(io, gpa, assets, &components, &count);

    const lib = editor.PlayBuild.buildPlayLibrary(io, a, path, &components, count, config) orelse {
        std.debug.print("Play library build failed\n", .{});
        return error.BuildFailed;
    };
    std.debug.print("[Turian] Play library: {s}\n", .{lib});
}

pub fn cmdImport(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !void {
    const result = project_ops.openProject(io, gpa, path);
    if (!result.valid) {
        std.debug.print("Not a Turian project (no project.json): {s}\n", .{path});
        return error.ProjectNotFound;
    }

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    asset_meta.scanAndEnsureMetas(io, gpa, assets);

    var db = editor.AssetDatabase.init(gpa);
    defer db.deinit();
    db.scan(io, assets);

    var tm = editor.TaskManager.init();
    var task = CliTask.start(&tm, .import, "Import assets");
    editor.asset_importer.importAll(io, gpa, path, &db, task.progress());
    tm.complete(task.id);
    task.printStatus();
}

/// Re-save every scene asset still using the deprecated single-material
/// `"material_guid"` mesh_renderer field, converting it to `"material_guids"`.
/// Scenes already in the current format are left untouched. Loading a scene
/// migrates the field in memory regardless (see `SceneIo.sceneCompToEngine`);
/// this command is for batch-persisting that migration across a project.
/// Currently covers only this one legacy field — the general project-version
/// migration entry point will grow into this same `migrate` verb.
pub fn cmdMigrate(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !void {
    const result = project_ops.openProject(io, gpa, path);
    if (!result.valid) {
        std.debug.print("Not a Turian project (no project.json): {s}\n", .{path});
        return error.ProjectNotFound;
    }

    var assets_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{path}) catch path;
    asset_meta.scanAndEnsureMetas(io, gpa, assets);

    var db = editor.AssetDatabase.init(gpa);
    defer db.deinit();
    db.scan(io, assets);

    var objects: [engine.scene.MAX_OBJECTS]engine.SceneNode = undefined;
    var scanned: usize = 0;
    var migrated: usize = 0;

    var it = db.enumerate(.scene);
    while (it.next()) |info| {
        scanned += 1;
        const bytes = std.Io.Dir.cwd().readFileAlloc(io, info.path, gpa, .unlimited) catch continue;
        defer gpa.free(bytes);
        if (std.mem.indexOf(u8, bytes, "\"material_guid\":") == null) continue;

        var count: usize = 0;
        if (!editor.scene_io.loadSceneFromBytes(gpa, bytes, &objects, &count)) continue;
        editor.scene_io.saveScene(io, info.path, &objects, count, gpa);
        migrated += 1;
        std.debug.print("Migrated: {s}\n", .{info.path});
    }

    std.debug.print("Scanned {d} scene(s), migrated {d}.\n", .{ scanned, migrated });
}

fn builtinCount(components: []const scanner.ComponentDef) usize {
    var n: usize = 0;
    for (components) |*c| if (c.is_builtin) {
        n += 1;
    };
    return n;
}
