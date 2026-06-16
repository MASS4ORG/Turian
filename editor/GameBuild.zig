/// Game build system — generates and compiles a standalone game executable
/// from the current scene and user scripts.  Pure logic with no GUI dependency.
const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const ComponentDef = @import("Scanner.zig").ComponentDef;
const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;
const AssetInfo = @import("AssetDatabase.zig").AssetInfo;
const asset_importer = @import("AssetImporter.zig");
const asset_packager = @import("AssetPackager.zig");
const Progress = @import("Progress.zig").Progress;
const codegen = @import("GameCodegen.zig");

pub const RuntimeConfig = codegen.RuntimeConfig;
pub const BuildConfig = codegen.BuildConfig;
pub const appendKtx2Module = codegen.appendKtx2Module;

/// Build the user game into <project>/.cache/zig-out/bin/game.
/// Blocks until compilation finishes.  Returns true on success.
pub fn buildGame(
    io: std.Io,
    project_path: []const u8,
    components: []const ComponentDef,
    component_count: usize,
    config: BuildConfig,
    progress: Progress,
) bool {
    if (progress.cancelled()) return false;
    progress.report(0.05, "Preparing build");

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const a = arena.allocator();
    buildGameInner(io, a, project_path, components, component_count, config, progress) catch |err| {
        std.debug.print("[Turian] Build failed: {any}\n", .{err});
        progress.report(1, "Build failed");
        return false;
    };
    progress.report(1, "Build complete");
    return true;
}

fn buildGameInner(
    io: std.Io,
    a: std.mem.Allocator,
    project_path: []const u8,
    components: []const ComponentDef,
    component_count: usize,
    config: BuildConfig,
    progress: Progress,
) !void {
    const cache_path = try std.fmt.allocPrint(a, "{s}/.cache", .{project_path});
    std.Io.Dir.cwd().createDirPath(io, cache_path) catch {};

    var rel_files: [64][]const u8 = undefined;
    var abs_files: [64][]const u8 = undefined;
    var src_count: usize = 0;
    for (components[0..component_count]) |*def| {
        if (def.is_builtin) continue;
        const src = def.sourceFile();
        if (src.len == 0) continue;
        var found = false;
        for (rel_files[0..src_count]) |s| {
            if (std.mem.eql(u8, s, src)) {
                found = true;
                break;
            }
        }
        if (!found and src_count < rel_files.len) {
            rel_files[src_count] = src;
            abs_files[src_count] = if (std.fs.path.isAbsolute(src))
                std.fmt.allocPrint(a, "{s}", .{src}) catch src
            else
                std.fmt.allocPrint(a, "{s}/{s}", .{ config.build_root, src }) catch src;
            src_count += 1;
        }
    }

    // Resolve the boot scene + window options from the project's ProjectSettings
    // asset (issue #13). Falls back to the conventional `assets/scene-01.json`
    // and default window options when no settings asset is present.
    const assets_dir = if (std.fs.path.isAbsolute(project_path))
        try std.fmt.allocPrint(a, "{s}/assets", .{project_path})
    else
        try std.fmt.allocPrint(a, "{s}/{s}/assets", .{ config.build_root, project_path });

    var runtime = RuntimeConfig{};
    resolveRuntime(io, a, assets_dir, &runtime);

    // Normalise all paths that will be embedded in generated Zig source files.
    // Backslashes inside string literals are invalid escape sequences.
    const gen_config = BuildConfig{
        .engine_root = try codegen.normPath(a, config.engine_root),
        .editor_root = try codegen.normPath(a, config.editor_root),
        .cgltf_wrap_c = try codegen.normPath(a, config.cgltf_wrap_c),
        .vendor_include = try codegen.normPath(a, config.vendor_include),
        .build_root = try codegen.normPath(a, config.build_root),
        .sdl3_lib = try codegen.normPath(a, config.sdl3_lib),
        .math_root = try codegen.normPath(a, config.math_root),
        .guid_root = try codegen.normPath(a, config.guid_root),
        .oap_root = try codegen.normPath(a, config.oap_root),
        .serde_root = try codegen.normPath(a, config.serde_root),
        .serde_compat_root = try codegen.normPath(a, config.serde_compat_root),
        .ktx2_root = try codegen.normPath(a, config.ktx2_root),
        .gpu_root = try codegen.absUnder(a, config.build_root, config.gpu_root),
        .gpu_sdl3_c = try codegen.absUnder(a, config.build_root, config.gpu_sdl3_c),
        .render_root = try codegen.absUnder(a, config.build_root, config.render_root),
        // The SDL3 include tree is a generated artifact, emitted as a path
        // relative to the editor's build root — absolutize it so the game build
        // (which runs in the project's .cache dir) can find the headers.
        .sdl3_include = try codegen.absUnder(a, config.build_root, config.sdl3_include),
    };
    for (0..src_count) |i| {
        abs_files[i] = try codegen.normPath(a, abs_files[i]);
    }

    progress.report(0.1, "Generating project");
    const gen_project = try codegen.normPath(a, project_path);
    const use_gpu = codegen.sdl3LibPath(a, config).len > 0 and config.sdl3_include.len > 0;
    const main_src = try codegen.generateMainZig(a, gen_project, rel_files[0..src_count], components, component_count, runtime, use_gpu);
    const main_path = try std.fmt.allocPrint(a, "{s}/main.zig", .{cache_path});
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = main_path, .data = main_src });

    const build_src = try codegen.generateBuildZig(a, gen_config, abs_files[0..src_count]);
    const build_zig_path = try std.fmt.allocPrint(a, "{s}/build.zig", .{cache_path});
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = build_zig_path, .data = build_src });

    // Cook all assets and package them into game.oap *before* compiling, so the
    // shipped game loads from the package instead of the loose assets/ folder.
    progress.report(0.25, "Packaging assets");
    packageAssets(io, a, project_path);

    if (progress.cancelled()) return error.Cancelled;

    progress.report(0.4, "Compiling game");
    std.debug.print("[Turian] Building game...\n", .{});
    const argv = [_][]const u8{ "zig", "build", "-Doptimize=Debug" };
    try spawnAndWaitIn(io, a, &argv, cache_path);

    const game_out = try std.fmt.allocPrint(a, "{s}/zig-out/bin/game", .{cache_path});
    std.debug.print("[Turian] Game built: {s}\n", .{game_out});
}

/// Resolve runtime config from the project's `ProjectSettings` asset and the
/// asset database: window title/size/vsync (issue #13) and the boot scene GUID
/// the game loads through the SceneManager (issue #22). Falls back to the
/// conventional `scene-01.json` (or the first scene asset) when no settings
/// asset selects a boot scene. All strings are allocated in `a`.
fn resolveRuntime(io: std.Io, a: std.mem.Allocator, assets_dir: []const u8, out: *RuntimeConfig) void {
    var db = AssetDatabase.init(a);
    defer db.deinit();
    db.scan(io, assets_dir);

    // Window options + explicit boot scene from ProjectSettings, if present.
    var it = db.enumerate(.project_settings);
    if (it.next()) |info| {
        if (readFileAlloc(io, a, info.path)) |bytes| {
            if (engine.ProjectSettings.loadFromBytes(a, bytes)) |ps| {
                if (ps.project.name.len > 0) out.title = a.dupe(u8, ps.project.name) catch out.title;
                out.width = ps.graphics.width;
                out.height = ps.graphics.height;
                out.vsync = ps.graphics.vsync;
                if (ps.first_scene.len > 0) {
                    if (Guid.parse(ps.first_scene)) |gid| {
                        if (db.findByGuid(gid)) |sinfo| {
                            if (sinfo.asset_type == .scene)
                                out.boot_scene_guid = a.dupe(u8, ps.first_scene) catch ""
                            else
                                std.debug.print("[Turian] first_scene {s} is not a scene asset\n", .{ps.first_scene});
                        } else std.debug.print("[Turian] Boot scene {s} not found in assets\n", .{ps.first_scene});
                    } else |_| std.debug.print("[Turian] Invalid first_scene GUID '{s}'\n", .{ps.first_scene});
                }
            } else |err| std.debug.print("[Turian] Failed to parse {s}: {any}\n", .{ info.path, err });
        }
    }

    // Fallback: pick scene-01.json if present, else the first scene asset.
    if (out.boot_scene_guid.len == 0) {
        var fallback: ?AssetInfo = null;
        var scenes = db.enumerate(.scene);
        while (scenes.next()) |sinfo| {
            if (std.mem.endsWith(u8, sinfo.path, "scene-01.json")) {
                fallback = sinfo;
                break;
            }
            if (fallback == null) fallback = sinfo;
        }
        if (fallback) |sinfo| {
            var gbuf: [64]u8 = undefined;
            const gstr = std.fmt.bufPrint(&gbuf, "{f}", .{sinfo.guid}) catch return;
            out.boot_scene_guid = a.dupe(u8, gstr) catch "";
        }
    }
}

/// Read an entire file into a buffer owned by `a`. Returns null on any error.
fn readFileAlloc(io: std.Io, a: std.mem.Allocator, path: []const u8) ?[]u8 {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    return reader.interface.allocRemaining(a, .unlimited) catch null;
}

/// Cook every asset into `<project>/.cache/assets`, then bundle those artifacts
/// into `<project>/.cache/game.oap`. Failures are logged but non-fatal so a
/// project with no assets still builds.
fn packageAssets(io: std.Io, a: std.mem.Allocator, project_path: []const u8) void {
    var db = AssetDatabase.init(a);
    defer db.deinit();

    const assets_dir = std.fmt.allocPrint(a, "{s}/assets", .{project_path}) catch return;
    db.scan(io, assets_dir);
    asset_importer.importAll(io, a, project_path, &db, Progress.none);

    const oap_path = std.fmt.allocPrint(a, "{s}/.cache/game.oap", .{project_path}) catch return;
    const n = asset_packager.packageProject(io, a, project_path, &db, oap_path) catch |err| {
        std.debug.print("[Turian] Asset packaging failed: {any}\n", .{err});
        return;
    };
    std.debug.print("[Turian] Packaged {d} asset(s) into {s}\n", .{ n, oap_path });
}

// ---------------------------------------------------------------------------
// Cross-platform process spawning

/// Spawn a subprocess in the given working directory and wait for it to finish.
pub fn spawnAndWaitIn(io: std.Io, a: std.mem.Allocator, argv: []const []const u8, work_dir: []const u8) !void {
    _ = a;
    var child = try std.process.spawn(io, .{
        .argv = argv,
        .cwd = .{ .path = work_dir },
    });
    const term = try child.wait(io);
    switch (term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
}

/// Spawn a subprocess and wait for it to finish (cwd inherited).
pub fn spawnAndWait(io: std.Io, a: std.mem.Allocator, argv: []const []const u8) !void {
    _ = a;
    var child = try std.process.spawn(io, .{ .argv = argv });
    const term = try child.wait(io);
    switch (term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
}
