/// Headless CLI entry point for the Turian editor.
/// Build-time paths are baked in; env vars can override them.
///
/// Usage:
///   turian-cli new-project <path> [name]
///   turian-cli info        <project-path>
///   turian-cli import      <project-path>
///   turian-cli build       <project-path>
const std = @import("std");
const editor = @import("editor");
const build_options = @import("turian_build_options");

const GameBuild = editor.GameBuild;
const project_ops = editor.project_ops;
const scanner = editor.scanner;
const asset_meta = editor.asset_meta;

/// Drives a long-running operation through the editor Task API while echoing
/// progress to stdout. Backed by a real `TaskManager` so the final task status
/// can be queried after the operation returns (headless task status).
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
        if (pct == self.last_pct) return; // avoid spamming identical percentages
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

    /// Print the task's final status line, e.g. "Build: Completed".
    fn printStatus(self: *CliTask) void {
        if (self.tm.get(self.id)) |t| {
            std.debug.print("{s}: {s}\n", .{ t.kind.text(), t.status.text() });
        }
    }
};

/// CLI entry point. Parses the first argument as a subcommand.
pub fn main(init: std.process.Init) !void {
    const io = init.io;
    const gpa = init.gpa;

    var args = try std.process.Args.Iterator.initAllocator(init.minimal.args, gpa);
    defer args.deinit();
    _ = args.next(); // skip executable name

    const cmd = args.next() orelse return printUsage();

    if (std.mem.eql(u8, cmd, "new-project")) {
        const path = args.next() orelse return printUsage();
        const proj_name = args.next() orelse std.fs.path.basename(path);
        return cmdNewProject(io, path, if (proj_name.len > 0) proj_name else "New Project");
    } else if (std.mem.eql(u8, cmd, "info")) {
        const path = args.next() orelse return printUsage();
        return cmdInfo(io, gpa, path);
    } else if (std.mem.eql(u8, cmd, "import")) {
        const path = args.next() orelse return printUsage();
        return cmdImport(io, gpa, path);
    } else if (std.mem.eql(u8, cmd, "build")) {
        const path = args.next() orelse return printUsage();
        return cmdBuild(io, gpa, path, init.environ_map);
    } else if (std.mem.eql(u8, cmd, "play-build")) {
        const path = args.next() orelse return printUsage();
        return cmdPlayBuild(io, gpa, path, init.environ_map);
    } else {
        printUsage();
        return error.UnknownCommand;
    }
}

fn printUsage() void {
    std.debug.print(
        \\turian-cli — Turian Engine headless editor
        \\
        \\Commands:
        \\  new-project <path> [name]   Create a new project at the given path
        \\  info        <project-path>  Print project metadata and component list
        \\  import      <project-path>  Import all assets (reports task progress)
        \\  build       <project-path>  Compile the project into a game executable
        \\  play-build  <project-path>  Compile the in-editor Play-mode library (issue #31)
        \\
        \\Env-var overrides for 'build' (optional; build-time paths used by default):
        \\  TURIAN_ENGINE_ROOT    Path to engine/root.zig
        \\  TURIAN_EDITOR_ROOT    Path to editor/root.zig
        \\  TURIAN_CGLTF_WRAP_C   Path to engine/vendor/cgltf_wrap.c
        \\  TURIAN_VENDOR_INCLUDE Path to engine/vendor/ directory
        \\  TURIAN_BUILD_ROOT     Repository root path
        \\  TURIAN_SDL3_LIB       Path to libSDL3 (optional)
        \\  TURIAN_MATH3D_ROOT    Path to math3d/src/root.zig
        \\
    , .{});
}

fn cmdNewProject(io: std.Io, path: []const u8, proj_name: []const u8) !void {
    project_ops.newProject(io, path, proj_name);
    std.debug.print("[Turian] Project '{s}' created at: {s}\n", .{ proj_name, path });
}

fn cmdInfo(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !void {
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

fn cmdBuild(io: std.Io, gpa: std.mem.Allocator, path: []const u8, environ: *const std.process.Environ.Map) !void {
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
    // Config strings (SDK-relative paths) live for the whole build; an arena
    // frees them in one shot and keeps the leak-checking allocator quiet.
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

/// Compile the Play-mode shared library headlessly (issue #31). Useful for CI:
/// it exercises the play codegen + user-script compilation without a display.
fn cmdPlayBuild(io: std.Io, gpa: std.mem.Allocator, path: []const u8, environ: *const std.process.Environ.Map) !void {
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

fn cmdImport(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !void {
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

fn builtinCount(components: []const scanner.ComponentDef) usize {
    var n: usize = 0;
    for (components) |*c| if (c.is_builtin) {
        n += 1;
    };
    return n;
}
