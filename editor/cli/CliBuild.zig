const std = @import("std");
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
    project_ops.newProject(io, path, proj_name, build_options.version);
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
        .fbx_wrap_c = build_options.fbx_wrap_c_path,
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

pub fn cmdPlayBuild(io: std.Io, gpa: std.mem.Allocator, path: []const u8, environ: *const std.process.Environ.Map, verify: bool) !void {
    const baked = GameBuild.BuildConfig{
        .engine_root = build_options.engine_root_path,
        .editor_root = build_options.editor_root_path,
        .cgltf_wrap_c = build_options.cgltf_wrap_c_path,
        .fbx_wrap_c = build_options.fbx_wrap_c_path,
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
    if (verify) try verifyPlayLibrary(lib);
}

/// Loads the freshly built play library and resolves every C-ABI symbol the
/// studio needs, so the load path can be exercised without a GUI session.
fn verifyPlayLibrary(lib_path: []const u8) !void {
    var lib = editor.DynLib.open(lib_path) catch |err| {
        std.debug.print("Play library failed to load: {t}\n", .{err});
        return error.BuildFailed;
    };
    defer lib.close();

    var missing: usize = 0;
    inline for (@typeInfo(editor.PlayBuild.symbols).@"struct".decls) |decl| {
        const name = @field(editor.PlayBuild.symbols, decl.name);
        if (lib.lookup(*const anyopaque, name) == null) {
            std.debug.print("  missing symbol: {s}\n", .{name});
            missing += 1;
        }
    }
    if (missing > 0) {
        std.debug.print("Play library is missing {d} symbol(s)\n", .{missing});
        return error.BuildFailed;
    }
    std.debug.print("[Turian] Play library verified: loaded, all symbols resolved.\n", .{});
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

/// Runs the ADR-0012 project version-check + migration flow: compares
/// `project.json`'s `turian_version` against this engine build, lists every
/// pending migration (`editor.project_migrations.all`) between the two, and
/// applies them in order. `--yes` runs unattended (CI-friendly); `--no` aborts
/// without changes; `--dry-run` only prints the pending list. With none of
/// the three flags, the pre-flight list is printed and nothing is applied.
pub fn cmdMigrate(
    io: std.Io,
    gpa: std.mem.Allocator,
    path: []const u8,
    args: *std.process.Args.Iterator,
    environ: *const std.process.Environ.Map,
) !void {
    var yes = false;
    var no = false;
    var dry_run = false;
    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--yes")) {
            yes = true;
        } else if (std.mem.eql(u8, a, "--no")) {
            no = true;
        } else if (std.mem.eql(u8, a, "--dry-run")) {
            dry_run = true;
        }
    }

    const result = project_ops.openProject(io, gpa, path);
    if (!result.valid) {
        std.debug.print("Not a Turian project (no project.json): {s}\n", .{path});
        return error.ProjectNotFound;
    }

    var cfg = editor.ProjectConfig.load(io, gpa, path) catch
        try editor.ProjectConfig.initDefault(gpa, "", build_options.version);
    defer cfg.deinit();

    const project_version = std.SemanticVersion.parse(cfg.turian_version) catch std.SemanticVersion{ .major = 0, .minor = 0, .patch = 0 };
    const engine_version = editor.PackageManager.parseEngineVersion(build_options.version);

    const home_var = if (@import("builtin").os.tag == .windows) "USERPROFILE" else "HOME";
    const home_dir = environ.get(home_var) orelse "";
    var settings = try editor.settings.Settings.init(gpa, home_dir, path);
    defer settings.deinit();
    settings.load(io);
    const check_minor = settings.getBool("project.version_check_minor", true);

    const migrations = editor.project_migrations;
    const direction = migrations.classify(project_version, engine_version, check_minor);

    switch (direction) {
        .up_to_date => {
            std.debug.print("Project is up to date (turian_version {s}).\n", .{cfg.turian_version});
            return;
        },
        .newer => {
            std.debug.print(
                "Warning: project's turian_version ({s}) is newer than this engine ({s})" ++
                    " — this build may be older than the one the project was last saved with.\n",
                .{ cfg.turian_version, build_options.version },
            );
            if (yes) {
                std.debug.print("Continuing (--yes).\n", .{});
            } else {
                std.debug.print("Nothing to migrate going backward. Re-run with --yes to proceed anyway.\n", .{});
            }
            return;
        },
        .older_minor, .older_major => {},
    }

    const pending = migrations.pendingFor(&migrations.all, project_version, engine_version);
    std.debug.print(
        "Project turian_version {s} is behind engine {s} — {d} pending migration(s):\n",
        .{ cfg.turian_version, build_options.version, pending.len },
    );
    for (pending, 0..) |m, i| {
        std.debug.print("  {d}. [{d}.{d}.{d}] {s}\n", .{ i + 1, m.to_version.major, m.to_version.minor, m.to_version.patch, m.summary });
        if (m.manual_steps.len > 0) std.debug.print("     Manual step: {s}\n", .{m.manual_steps});
    }

    if (dry_run) return;
    if (no or !yes) {
        std.debug.print("Re-run with --yes to apply, or --no to acknowledge and skip.\n", .{});
        return;
    }

    const ctx = migrations.Context{ .io = io, .allocator = gpa, .project_path = path };
    const run_result = migrations.run(pending, project_version, ctx);
    if (run_result.failed_at) |idx| {
        std.debug.print("Migration {d}/{d} failed: {s}\n", .{ idx + 1, pending.len, @errorName(run_result.err.?) });
        return run_result.err.?;
    }

    const a = gpa;
    a.free(cfg.turian_version);
    cfg.turian_version = try a.dupe(u8, build_options.version);
    try cfg.save(io, path);
    std.debug.print("Ran {d} migration(s). Project updated to turian_version {s}.\n", .{ run_result.ran, build_options.version });
}

fn builtinCount(components: []const scanner.ComponentDef) usize {
    var n: usize = 0;
    for (components) |*c| if (c.is_builtin) {
        n += 1;
    };
    return n;
}
