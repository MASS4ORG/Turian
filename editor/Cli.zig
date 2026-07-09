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
const engine = @import("engine");
const rdebug = @import("debug");
const rmcp = @import("mcp");
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
    } else if (std.mem.eql(u8, cmd, "debug")) {
        const sub = args.next() orelse return printUsageDebug();
        return cmdDebug(io, gpa, sub, &args);
    } else if (std.mem.eql(u8, cmd, "mcp")) {
        return cmdMcp(io, gpa, &args);
    } else if (std.mem.eql(u8, cmd, "docs")) {
        const sub = args.next() orelse return printUsageDocs();
        return cmdDocs(io, gpa, sub, &args);
    } else if (std.mem.eql(u8, cmd, "package")) {
        const sub = args.next() orelse return printUsagePackage();
        return cmdPackage(io, gpa, sub, &args, init.environ_map);
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
        \\  play-build  <project-path>  Compile the in-editor Play-mode library
        \\  debug       <subcommand>    Connect to a running Turian debug server
        \\  mcp                         Start an MCP server (stdio) backed by the debug server
        \\  docs        <subcommand>    Generate AI context or documentation
        \\  package     <subcommand>    Manage project packages
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

fn printUsageDebug() void {
    std.debug.print(
        \\turian-cli debug — Remote debug client (connects to a running game/studio)
        \\
        \\Usage:  turian-cli debug <subcommand> [--host 127.0.0.1] [--port 7777] [--token <t>]
        \\
        \\Subcommands:
        \\  connect              Test connection and print server info
        \\  scenes               List all loaded scenes
        \\  entities [--scene S] List entities in the active (or named) scene
        \\  inspect  <name>      Full detail for entity <name>
        \\  component <entity> <component>  Dump a single component's fields
        \\  snapshot             Dump a full engine snapshot to stdout
        \\  schema               Print the built-in component schema
        \\  metrics              Print live runtime metrics (FPS, memory, draws)
        \\  profiler             Capture the latest profiler frame
        \\  memory               Print allocator memory usage
        \\  errors               List recent engine warnings/errors
        \\  assets               List project assets (guid, path, type)
        \\  watch [event...]     Subscribe to runtime events and stream them
        \\                       (e.g. entity.created fps.changed; omit = all)
        \\  record <file>        Record all events to a JSONL file (Ctrl-C to stop)
        \\  replay <file>        Re-send recorded requests from a JSONL file
        \\
        \\Mutations (require the server in read-write mode):
        \\  set <entity> <component> <field> <value>   Set a component field
        \\  spawn <name>         Create a new empty entity
        \\  destroy <name>       Remove an entity by name
        \\
        \\Machine-driven UI interaction (Studio only, read-write mode; applied
        \\the frame after the call — dvui needs events before it builds widgets):
        \\  mousemove <x> <y>            Move the synthetic mouse cursor
        \\  click <x> <y> [button]       Move + press + release (button: left/right/middle, default left)
        \\  key <code> [up]              Key down (default) or up — code is a dvui.enums.Key name (e.g. "a", "enter")
        \\  text <str>                   Synthesize a text-input event
        \\  capture                      Schedule a whole-window screenshot (see `screenshot`)
        \\  screenshot                   Poll the last whole-window screenshot's result/path
        \\
    , .{});
}

/// Parses a CLI value token into a debug-protocol JSON value literal: numbers
/// stay numeric, true/false become booleans, everything else becomes a string.
fn valueLiteral(buf: []u8, raw: []const u8) []const u8 {
    if (std.mem.eql(u8, raw, "true")) return "true";
    if (std.mem.eql(u8, raw, "false")) return "false";
    if (std.fmt.parseFloat(f64, raw)) |_| {
        return raw; // numeric literal as-is
    } else |_| {}
    const out = std.fmt.bufPrint(buf, "\"{s}\"", .{raw}) catch return "\"\"";
    return out;
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
        .ui_render_root = build_options.ui_render_root_path,
        .dvui_url = build_options.dvui_url,
        .dvui_hash = build_options.dvui_hash,
        .engine_version = build_options.version,
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

/// Compile the Play-mode shared library headlessly. Useful for CI:
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

// ── debug subcommands ─────────────────────────────────────────────────────────

fn cmdDebug(
    io: std.Io,
    gpa: std.mem.Allocator,
    sub: []const u8,
    args: *std.process.Args.Iterator,
) !void {
    // Parse common flags: --host, --port, --token, and sub-specific positionals.
    var host_buf: [256]u8 = std.mem.zeroes([256]u8);
    @memcpy(host_buf[0..9], "127.0.0.1");
    var host: []u8 = host_buf[0..9];
    var port: u16 = rdebug.Protocol.DEFAULT_PORT;
    var token: []const u8 = "";
    var extra1: []const u8 = "";
    var extra2: []const u8 = "";
    var extra3: []const u8 = "";
    var extra4: []const u8 = "";

    while (args.next()) |arg| {
        if (std.mem.eql(u8, arg, "--host")) {
            const h = args.next() orelse continue;
            const len = @min(h.len, host_buf.len);
            @memcpy(host_buf[0..len], h[0..len]);
            host = host_buf[0..len];
        } else if (std.mem.eql(u8, arg, "--port")) {
            if (args.next()) |ps| port = std.fmt.parseInt(u16, ps, 10) catch port;
        } else if (std.mem.eql(u8, arg, "--token")) {
            token = args.next() orelse "";
        } else if (extra1.len == 0) {
            extra1 = arg;
        } else if (extra2.len == 0) {
            extra2 = arg;
        } else if (extra3.len == 0) {
            extra3 = arg;
        } else if (extra4.len == 0) {
            extra4 = arg;
        }
    }

    var client = rdebug.Client.connect(io, host, port) catch |err| {
        std.debug.print("Cannot connect to {s}:{d}: {s}\n", .{ host, port, @errorName(err) });
        std.debug.print("Is a Turian game or studio running with the debug server enabled?\n", .{});
        return error.ConnectionFailed;
    };
    defer client.close();

    if (token.len > 0) {
        const ok = client.auth(gpa, token) catch false;
        if (!ok) {
            std.debug.print("Authentication failed\n", .{});
            return error.AuthFailed;
        }
    }

    const w = std.Io.File.stderr().writer(io, &[_]u8{});
    _ = w;

    if (std.mem.eql(u8, sub, "connect")) {
        const resp = try client.call(gpa, "ping", null);
        defer gpa.free(resp);
        if (std.mem.indexOf(u8, resp, "pong") != null)
            std.debug.print("Connected to {s}:{d} — server is up\n", .{ host, port })
        else
            std.debug.print("Unexpected response: {s}\n", .{resp});
    } else if (std.mem.eql(u8, sub, "scenes")) {
        const resp = try client.call(gpa, "scene.list", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "entities")) {
        const params: ?[]const u8 = if (extra1.len > 0)
            try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1})
        else
            null;
        defer if (params) |p| gpa.free(p);
        const resp = try client.call(gpa, "entity.find", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "inspect")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.inspect", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "component")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\",\"component\":\"{s}\"}}", .{ extra1, extra2 });
        defer gpa.free(params);
        const resp = try client.call(gpa, "component.get", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "snapshot")) {
        const resp = try client.call(gpa, "snapshot", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "schema")) {
        const resp = try client.call(gpa, "schema", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "metrics")) {
        const resp = try client.call(gpa, "metrics", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "profiler")) {
        const resp = try client.call(gpa, "profiler.capture", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "memory")) {
        const resp = try client.call(gpa, "memory", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "errors")) {
        const resp = try client.call(gpa, "errors", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "assets")) {
        const resp = try client.call(gpa, "asset.list", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "set")) {
        if (extra1.len == 0 or extra2.len == 0 or extra3.len == 0 or extra4.len == 0) return printUsageDebug();
        var vbuf: [256]u8 = undefined;
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\",\"component\":\"{s}\",\"field\":\"{s}\",\"value\":{s}}}", .{ extra1, extra2, extra3, valueLiteral(&vbuf, extra4) });
        defer gpa.free(params);
        const resp = try client.call(gpa, "component.set", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "spawn")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.spawn", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "destroy")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.destroy", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "click")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const button = if (extra3.len > 0) extra3 else "left";
        const params = try std.fmt.allocPrint(gpa, "{{\"x\":{s},\"y\":{s},\"button\":\"{s}\"}}", .{ extra1, extra2, button });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.click", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "mousemove")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"x\":{s},\"y\":{s}}}", .{ extra1, extra2 });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.mouseMove", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "key")) {
        if (extra1.len == 0) return printUsageDebug();
        const down = extra2.len == 0 or !std.mem.eql(u8, extra2, "up");
        const params = try std.fmt.allocPrint(gpa, "{{\"code\":\"{s}\",\"down\":{s}}}", .{ extra1, if (down) "true" else "false" });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.key", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "text")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"text\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.text", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "capture")) {
        const resp = try client.call(gpa, "screenshot.capture", "{}");
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "screenshot")) {
        const resp = try client.call(gpa, "screenshot.last", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "watch")) {
        var events: [4][]const u8 = undefined;
        var n: usize = 0;
        for ([_][]const u8{ extra1, extra2, extra3, extra4 }) |e| {
            if (e.len > 0) {
                events[n] = e;
                n += 1;
            }
        }
        std.debug.print("Watching events (Ctrl-C to stop)...\n", .{});
        var wbuf: [4096]u8 = undefined;
        var stdout = std.Io.File.stdout().writer(io, &wbuf);
        try client.watch(gpa, events[0..n], &stdout.interface);
    } else if (std.mem.eql(u8, sub, "record")) {
        if (extra1.len == 0) return printUsageDebug();
        var file = std.Io.Dir.cwd().createFile(io, extra1, .{}) catch {
            std.debug.print("Cannot create file: {s}\n", .{extra1});
            return error.RecordFailed;
        };
        defer file.close(io);
        std.debug.print("Recording session to {s} (Ctrl-C to stop)...\n", .{extra1});
        var fbuf: [4096]u8 = undefined;
        var fw = file.writer(io, &fbuf);
        try client.record(gpa, &fw.interface);
    } else if (std.mem.eql(u8, sub, "replay")) {
        if (extra1.len == 0) return printUsageDebug();
        const jsonl = std.Io.Dir.cwd().readFileAlloc(io, extra1, gpa, .limited(16 * 1024 * 1024)) catch {
            std.debug.print("Cannot read file: {s}\n", .{extra1});
            return error.ReplayFailed;
        };
        defer gpa.free(jsonl);
        var wbuf: [4096]u8 = undefined;
        var stdout = std.Io.File.stdout().writer(io, &wbuf);
        const sent = try client.replay(gpa, jsonl, &stdout.interface);
        std.debug.print("Replayed {d} request(s)\n", .{sent});
    } else {
        printUsageDebug();
        return error.UnknownDebugSubcommand;
    }
}

fn printResponseStderr(allocator: std.mem.Allocator, resp: []const u8) !void {
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, resp, .{}) catch {
        std.debug.print("{s}\n", .{resp});
        return;
    };
    defer parsed.deinit();
    const obj = if (parsed.value == .object) parsed.value.object else {
        std.debug.print("{s}\n", .{resp});
        return;
    };
    if (obj.get("error")) |err_val| {
        if (err_val == .object) {
            const msg = if (err_val.object.get("message")) |m| (if (m == .string) m.string else "?") else "?";
            const code = if (err_val.object.get("code")) |c| (if (c == .integer) c.integer else 0) else @as(i64, 0);
            std.debug.print("Error {d}: {s}\n", .{ code, msg });
        }
        return;
    }
    if (obj.get("result")) |result| {
        var out: std.Io.Writer.Allocating = .init(allocator);
        defer out.deinit();
        var jw = std.json.Stringify{ .writer = &out.writer, .options = .{ .whitespace = .indent_2 } };
        jw.write(result) catch {};
        std.debug.print("{s}\n", .{out.written()});
    }
}

// ── mcp subcommand ────────────────────────────────────────────────────────────

fn cmdMcp(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    var host_buf: [256]u8 = std.mem.zeroes([256]u8);
    @memcpy(host_buf[0..9], "127.0.0.1");
    var host_len: usize = 9;
    var port: u16 = rdebug.Protocol.DEFAULT_PORT;
    var token: []const u8 = "";

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--host")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, host_buf.len - 1);
            @memcpy(host_buf[0..n], v[0..n]);
            host_len = n;
        } else if (std.mem.eql(u8, a, "--port")) {
            const v = args.next() orelse return error.MissingArg;
            port = std.fmt.parseInt(u16, v, 10) catch return error.InvalidPort;
        } else if (std.mem.eql(u8, a, "--token")) {
            token = args.next() orelse return error.MissingArg;
        }
    }

    std.debug.print("[turian-mcp] connecting to {s}:{d}\n", .{ host_buf[0..host_len], port });
    try rmcp.run(io, gpa, .{
        .host = host_buf[0..host_len],
        .port = port,
        .token = token,
    });
}

// ── docs subcommands ──────────────────────────────────────────────────────────

fn printUsageDocs() void {
    std.debug.print(
        \\Usage:  turian-cli docs <subcommand> [--out <dir>]
        \\
        \\Subcommands:
        \\  export-ai-context   Generate a self-contained AI knowledge pack (no game needed)
        \\
        \\Flags:
        \\  --out <dir>   Output directory (default: .turian)
        \\
    , .{});
}

fn cmdDocs(io: std.Io, gpa: std.mem.Allocator, sub: []const u8, args: *std.process.Args.Iterator) !void {
    if (std.mem.eql(u8, sub, "export-ai-context")) {
        return cmdDocsExportAiContext(io, gpa, args);
    }
    printUsageDocs();
    return error.UnknownSubcommand;
}

fn cmdDocsExportAiContext(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    var out_dir_buf: [512]u8 = std.mem.zeroes([512]u8);
    var out_dir_len: usize = 8; // ".turian/"
    @memcpy(out_dir_buf[0..out_dir_len], ".turian/");

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--out")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, out_dir_buf.len - 2);
            @memcpy(out_dir_buf[0..n], v[0..n]);
            if (out_dir_buf[n - 1] != '/') {
                out_dir_buf[n] = '/';
                out_dir_len = n + 1;
            } else {
                out_dir_len = n;
            }
        }
    }

    const out_path = out_dir_buf[0..out_dir_len];
    std.debug.print("Exporting AI context to {s}\n", .{out_path});

    const cwd = std.Io.Dir.cwd();
    try cwd.createDirPath(io, std.mem.trimEnd(u8, out_path, "/"));

    try writeAiContextFile(io, gpa, cwd, out_path, "engine-overview.md", aiContextOverview);
    try writeAiContextFile(io, gpa, cwd, out_path, "component-schema.json", null);
    try writeAiContextFile(io, gpa, cwd, out_path, "protocol-reference.json", aiContextProtocol);
    try writeAiContextFile(io, gpa, cwd, out_path, "mcp-tools.json", null);

    const examples_path = try std.fmt.allocPrint(gpa, "{s}examples/", .{out_path});
    defer gpa.free(examples_path);
    try cwd.createDirPath(io, std.mem.trimEnd(u8, examples_path, "/"));
    try writeAiContextFile(io, gpa, cwd, examples_path, "list-scenes.json", exampleListScenes);
    try writeAiContextFile(io, gpa, cwd, examples_path, "inspect-entity.json", exampleInspectEntity);

    // component-schema.json, mcp-tools.json, asset-schema.json and events.json
    // need the engine/editor serialisers.
    try writeComponentSchema(io, gpa, cwd, out_path);
    try writeMcpTools(io, gpa, cwd, out_path);
    try writeAssetSchema(io, gpa, cwd, out_path);
    try writeEventCatalog(io, gpa, cwd, out_path);

    std.debug.print("Done. Add to CLAUDE.md:\n  @{s}engine-overview.md\n", .{out_path});
}

fn writeAiContextFile(
    io: std.Io,
    gpa: std.mem.Allocator,
    cwd: std.Io.Dir,
    dir: []const u8,
    name: []const u8,
    content: ?[]const u8,
) !void {
    if (content == null) return; // handled separately
    const path = try std.fmt.allocPrint(gpa, "{s}{s}", .{ dir, name });
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = content.? });
    std.debug.print("  wrote {s}{s}\n", .{ dir, name });
}

fn writeComponentSchema(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try engine.introspect.writeSchema(&jw);
    const path = try std.fmt.allocPrint(gpa, "{s}component-schema.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}component-schema.json\n", .{dir});
}

fn writeMcpTools(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try jw.beginObject();
    try jw.objectField("tools");
    try jw.beginArray();
    for (rmcp.Tools.ALL) |t| {
        try jw.beginObject();
        try jw.objectField("name");
        try jw.write(t.name);
        try jw.objectField("description");
        try jw.write(t.description);
        try jw.objectField("debug_method");
        try jw.write(t.debug_method orelse "");
        try jw.objectField("mutates");
        try jw.write(t.mutates);
        try jw.objectField("inputSchema");
        try jw.beginWriteRaw();
        try jw.writer.writeAll(t.input_schema);
        jw.endWriteRaw();
        try jw.endObject();
    }
    try jw.endArray();
    try jw.endObject();
    const path = try std.fmt.allocPrint(gpa, "{s}mcp-tools.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}mcp-tools.json\n", .{dir});
}

fn writeAssetSchema(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try jw.beginObject();
    try jw.objectField("asset_types");
    try jw.beginArray();
    inline for (@typeInfo(editor.AssetType).@"enum".fields) |f| try jw.write(f.name);
    try jw.endArray();
    try jw.endObject();
    const path = try std.fmt.allocPrint(gpa, "{s}asset-schema.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}asset-schema.json\n", .{dir});
}

fn writeEventCatalog(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try engine.introspect.writeEventCatalog(&jw);
    const path = try std.fmt.allocPrint(gpa, "{s}events.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}events.json\n", .{dir});
}

const aiContextOverview =
    \\# Turian Engine — AI Context Overview
    \\
    \\Turian is a Zig game engine. This document is machine-generated for LLM context.
    \\
    \\## Architecture
    \\
    \\- **engine/** — core types: SceneNode, Component, Transform, assets, ECS
    \\- **debug/** — Remote Debug Protocol: JSON-RPC 2.0 over TCP (port 7777 game, 7778 Studio)
    \\- **mcp/** — MCP adapter: stdio JSON-RPC 2.0, version 2024-11-05
    \\- **editor/** — CLI tools: `turian-cli`
    \\- **studio/** — GUI editor: `turian-studio`
    \\- **render/** — GPU renderer (SDL3-GPU, SPIRV shaders)
    \\
    \\## Connecting to a Live Session
    \\
    \\Add to `.mcp.json`:
    \\```json
    \\{
    \\  "mcpServers": {
    \\    "turian-game":   { "command": "turian-cli", "args": ["mcp"] },
    \\    "turian-studio": { "command": "turian-cli", "args": ["mcp", "--port", "7778"] }
    \\  }
    \\}
    \\```
    \\
    \\The game must link the `debug` module and call `debug.Server.start()`.
    \\The Studio starts its server automatically on port 7778.
    \\
    \\## Key Concepts
    \\
    \\### SceneNode
    \\Every object in a scene. Has: name (max 64 chars), transform (position/rotation/scale),
    \\active flag, parent index, and up to 8 components.
    \\
    \\### Component
    \\Tagged union over all built-in types (Camera, Light, MeshRenderer, RigidBody,
    \\Collider, AudioSource, Animator) plus user scripts. See component-schema.json for fields.
    \\
    \\### Transform
    \\position: [3]f32, rotation: Quaternion ([4]f32 xyzw), scale: [3]f32.
    \\
    \\### Metrics
    \\fps, frame_time_ms, frame_count, memory_bytes, allocation_count,
    \\draw_calls, triangles, gpu_time_ms, scene_count, entity_count, component_count.
    \\
    \\## Available MCP Tools
    \\
    \\See mcp-tools.json for the full list with schemas.
    \\Quick reference:
    \\- Read: list_scenes, inspect_scene, find_entities, scene_summary,
    \\  inspect_entity, get_component, get_metrics, get_schema,
    \\  list_assets, inspect_material, capture_profiler, inspect_memory, list_errors
    \\- Write (read-write mode + confirm): modify_component, set_transform,
    \\  spawn_entity, destroy_entity, reload_asset
    \\
    \\## Events
    \\
    \\Clients can `subscribe` to runtime events and receive JSON-RPC notifications.
    \\See events.json for the catalog (entity.created, entity.destroyed,
    \\scene.loaded, scene.unloaded, resource.reloaded, fps.changed).
    \\
    \\## Debug Protocol Methods
    \\
    \\See protocol-reference.json. All MCP tools map 1:1 to debug methods.
    \\
    \\## Safety
    \\
    \\Reads are always available. Mutating methods require the debug server started
    \\in read-write mode (CLI `--rw` / `allow_write`); otherwise they return a
    \\READONLY (-32001) error. The Studio runs read-write so LLM tools can edit the
    \\open scene (all edits go through the editor's undo stack). The MCP layer adds a
    \\confirmation gate: a mutating tool first returns a preview and only applies on
    \\a second call with `confirm: true`. A per-session `session.readonly` request
    \\and an optional per-connection rate limit provide further guardrails.
    \\
;

const aiContextProtocol =
    \\{
    \\  "protocol": "Turian Remote Debug Protocol",
    \\  "transport": "JSON-RPC 2.0 over TCP, newline-delimited",
    \\  "default_port_game": 7777,
    \\  "default_port_studio": 7778,
    \\  "methods": {
    \\    "ping": { "params": null, "result": "\"pong\"" },
    \\    "auth": { "params": { "token": "string" }, "result": "\"ok\"" },
    \\    "scene.list": { "params": null, "result": "array of scene objects" },
    \\    "scene.inspect": { "params": { "name": "string?" }, "result": "scene with nodes" },
    \\    "entity.find": { "params": { "name": "string?", "component": "string?" }, "result": "array of entity summaries" },
    \\    "entity.inspect": { "params": { "name": "string | index: integer" }, "result": "entity detail with transform + components" },
    \\    "component.get": { "params": { "entity": "string", "component": "string" }, "result": "component fields object" },
    \\    "component.set": { "params": { "entity": "string", "component": "string", "field": "string", "value": "any" }, "result": "{ ok, message }", "note": "mutating; requires read-write server" },
    \\    "transform.set": { "params": { "entity": "string", "channel": "position|rotation|scale", "value": "[x,y,z]" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "entity.spawn": { "params": { "name": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "entity.destroy": { "params": { "entity": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "asset.list": { "params": null, "result": "array of { guid, path, type }" },
    \\    "asset.inspect": { "params": { "guid": "string" }, "result": "{ guid, path, type }" },
    \\    "asset.reload": { "params": { "guid": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "snapshot": { "params": null, "result": "full world snapshot" },
    \\    "schema": { "params": null, "result": "component type catalog" },
    \\    "metrics": { "params": null, "result": "runtime performance counters" },
    \\    "profiler.capture": { "params": null, "result": "latest profiler frame (counters + zones)" },
    \\    "memory": { "params": null, "result": "{ memory_bytes, allocation_count }" },
    \\    "errors": { "params": null, "result": "array of recent warn/err log entries" },
    \\    "subscribe": { "params": { "event": "string | \"*\"" }, "result": "\"ok\"", "note": "streams notifications" },
    \\    "unsubscribe": { "params": { "event": "string | \"*\"" }, "result": "\"ok\"" },
    \\    "session.readonly": { "params": null, "result": "\"ok\"", "note": "drops this session's write rights" }
    \\  },
    \\  "error_codes": {
    \\    "-32700": "PARSE_ERROR",
    \\    "-32600": "INVALID_REQUEST",
    \\    "-32601": "METHOD_NOT_FOUND",
    \\    "-32602": "INVALID_PARAMS",
    \\    "-32603": "INTERNAL_ERROR",
    \\    "-32000": "NOT_FOUND",
    \\    "-32001": "READONLY",
    \\    "-32002": "RATE_LIMITED"
    \\  }
    \\}
    \\
;

const exampleListScenes =
    \\{
    \\  "request":  { "jsonrpc": "2.0", "id": 1, "method": "scene.list" },
    \\  "response": { "jsonrpc": "2.0", "id": 1, "result": [
    \\    { "name": "Main", "id": "main.scene", "active": true, "node_count": 12 }
    \\  ]}
    \\}
    \\
;

const exampleInspectEntity =
    \\{
    \\  "request":  { "jsonrpc": "2.0", "id": 2, "method": "entity.inspect", "params": { "name": "Player" } },
    \\  "response": { "jsonrpc": "2.0", "id": 2, "result": {
    \\    "index": 3,
    \\    "name": "Player",
    \\    "active": true,
    \\    "transform": { "position": [0,1,0], "rotation": [0,0,0,1], "scale": [1,1,1] },
    \\    "components": [
    \\      { "type": "RigidBody", "tag": "rigid_body", "fields": { "mass": 1.0, "use_gravity": true } }
    \\    ]
    \\  }}
    \\}
    \\
;

// ── package subcommands ───────────────────────────────────────────────────────

fn printUsagePackage() void {
    std.debug.print(
        \\turian-cli package — Manage project packages
        \\
        \\Usage:  turian-cli package <subcommand> [--project <path>] [args]
        \\
        \\Subcommands:
        \\  install <source>    Install a package (local path or git URL)
        \\  remove  <name>      Remove an installed package by name
        \\  update  [name]      Update a package (or all packages)
        \\  list                List installed packages
        \\  info    <name>      Show full manifest for an installed package
        \\  search  <query>     Search the package registry (not yet available — see #65)
        \\
        \\Flags:
        \\  --project <path>    Project root directory (default: current directory)
        \\  --vendored          Install into <project>/packages/ instead of the
        \\                      shared store (for committing/offline use)
        \\
        \\By default packages install into the central store
        \\($TURIAN_PACKAGE_HOME, else ~/.cache/turian/packages), shared across
        \\projects, and are recorded in project.json.
        \\
    , .{});
}

fn cmdPackage(
    io: std.Io,
    gpa: std.mem.Allocator,
    sub: []const u8,
    args: *std.process.Args.Iterator,
    environ: *const std.process.Environ.Map,
) !void {
    // Parse --project / --vendored flags and any positional arguments.
    var project_buf: [512]u8 = std.mem.zeroes([512]u8);
    project_buf[0] = '.';
    var project_len: usize = 1;
    var arg1: []const u8 = "";
    var vendored = false;

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--project")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, project_buf.len - 1);
            @memcpy(project_buf[0..n], v[0..n]);
            project_len = n;
        } else if (std.mem.eql(u8, a, "--vendored") or std.mem.eql(u8, a, "--local")) {
            vendored = true;
        } else if (arg1.len == 0) {
            arg1 = a;
        }
    }
    const project_path = project_buf[0..project_len];

    // Resolve the central package store root (default install location).
    const store_root = editor.package_store.resolveRoot(gpa, environ) catch "";
    defer if (store_root.len > 0) gpa.free(store_root);

    if (std.mem.eql(u8, sub, "install")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageInstall(io, gpa, project_path, arg1, store_root, vendored);
    } else if (std.mem.eql(u8, sub, "remove")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageRemove(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "update")) {
        return cmdPackageUpdate(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "list")) {
        return cmdPackageList(io, gpa, project_path, store_root);
    } else if (std.mem.eql(u8, sub, "info")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageInfo(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "search")) {
        std.debug.print(
            \\Package registry search is not yet available.
            \\A registry/repository API is planned in issue #65.
            \\In the meantime, install packages from a local path or git URL:
            \\  turian-cli package install /path/to/package
            \\  turian-cli package install git+https://example.com/my-package
            \\
        , .{});
        return;
    } else {
        printUsagePackage();
        return error.UnknownSubcommand;
    }
}

/// Install a package from `source` (local path or `git+https://…` URL).
/// Default: into the central store `<store>/<name>/<version>`, recorded in
/// `project.json` `packages`. With `--vendored`: copied into `<project>/packages/`.
fn cmdPackageInstall(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    source: []const u8,
    store_root: []const u8,
    vendored: bool,
) !void {
    const is_git = std.mem.startsWith(u8, source, "git+http") or
        std.mem.startsWith(u8, source, "http://") or
        std.mem.startsWith(u8, source, "https://");

    // Materialize the source into a local directory we can read the manifest
    // from: git sources are cloned into a scratch dir first.
    var scratch: ?[]const u8 = null;
    defer if (scratch) |s| {
        std.Io.Dir.cwd().deleteTree(io, s) catch {};
        gpa.free(s);
    };
    const src_dir: []const u8 = if (is_git) blk: {
        const git_url = if (std.mem.startsWith(u8, source, "git+")) source[4..] else source;
        const tmp = try std.fmt.allocPrint(gpa, "{s}/.cache/clone-{x}", .{
            if (store_root.len > 0) store_root else ".",
            std.hash.Wyhash.hash(0, source),
        });
        std.Io.Dir.cwd().deleteTree(io, tmp) catch {};
        std.debug.print("[Turian] Cloning {s}\n", .{git_url});
        const argv = [_][]const u8{ "git", "clone", "--depth=1", git_url, tmp };
        editor.GameBuild.spawnAndWait(io, gpa, &argv) catch |err| {
            std.debug.print("[Turian] git clone failed: {s}\n", .{@errorName(err)});
            gpa.free(tmp);
            return error.InstallFailed;
        };
        scratch = tmp;
        break :blk tmp;
    } else source;

    // Validate the manifest and learn the package name + version.
    var mpath_buf: [1024]u8 = undefined;
    const manifest_path = std.fmt.bufPrint(&mpath_buf, "{s}/turian-package.json", .{src_dir}) catch
        return error.PathTooLong;
    const manifest_bytes = std.Io.Dir.cwd().readFileAlloc(io, manifest_path, gpa, .limited(64 * 1024)) catch {
        std.debug.print("[Turian] No turian-package.json found at: {s}\n", .{src_dir});
        return error.InvalidPackage;
    };
    defer gpa.free(manifest_bytes);
    const manifest = editor.PackageManifest.parse(gpa, manifest_bytes) catch |err| {
        std.debug.print("[Turian] Invalid turian-package.json: {s}\n", .{@errorName(err)});
        return error.InvalidPackage;
    };
    defer manifest.deinit();

    if (vendored or store_root.len == 0) {
        try installVendored(io, gpa, project_path, src_dir, manifest.name, manifest.version);
    } else {
        try installToStore(io, gpa, project_path, src_dir, source, is_git, store_root, manifest.name, manifest.version);
    }
}

/// Copy a package into the central store and record it in `project.json`.
fn installToStore(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    src_dir: []const u8,
    source: []const u8,
    is_git: bool,
    store_root: []const u8,
    name: []const u8,
    version: []const u8,
) !void {
    const dest = try editor.package_store.packagePath(gpa, store_root, name, version);
    defer gpa.free(dest);

    if (editor.package_store.isInstalled(io, gpa, store_root, name, version)) {
        std.debug.print("[Turian] '{s}' v{s} already in store ({s})\n", .{ name, version, dest });
    } else {
        std.debug.print("[Turian] Installing '{s}' v{s} into store\n", .{ name, version });
        copyDir(io, gpa, src_dir, dest) catch |err| {
            std.debug.print("[Turian] Copy to store failed: {s}\n", .{@errorName(err)});
            return error.InstallFailed;
        };
    }

    // Record the dependency in project.json so the build/discovery resolve it.
    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();
    cfg.addPackage(io, project_path, name, version, source, is_git) catch |err| {
        std.debug.print("[Turian] Failed to record package in project.json: {s}\n", .{@errorName(err)});
        return error.InstallFailed;
    };
    std.debug.print("[Turian] '{s}' v{s} added to {s}/project.json\n", .{ name, version, project_path });
}

/// Copy a package directly into the project's `packages/` dir (no store, not
/// recorded in project.json — discovered by directory scan). For vendoring.
fn installVendored(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    src_dir: []const u8,
    name: []const u8,
    version: []const u8,
) !void {
    var pkg_dir_buf: [512]u8 = undefined;
    const packages_path = std.fmt.bufPrint(&pkg_dir_buf, "{s}/packages", .{project_path}) catch
        return error.PathTooLong;
    std.Io.Dir.cwd().createDirPath(io, packages_path) catch {};

    const dest = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ packages_path, name });
    defer gpa.free(dest);
    {
        var existing = std.Io.Dir.cwd().openDir(io, dest, .{}) catch null;
        if (existing) |*d| {
            d.close(io);
            std.debug.print("[Turian] Package '{s}' is already vendored at {s}\n", .{ name, dest });
            return error.AlreadyInstalled;
        }
    }
    std.debug.print("[Turian] Vendoring '{s}' v{s} into {s}\n", .{ name, version, dest });
    copyDir(io, gpa, src_dir, dest) catch |err| {
        std.debug.print("[Turian] Copy failed: {s}\n", .{@errorName(err)});
        return error.InstallFailed;
    };
}

/// Recursively copy a directory tree from `src` to `dst`.
fn copyDir(io: std.Io, gpa: std.mem.Allocator, src: []const u8, dst: []const u8) !void {
    std.Io.Dir.cwd().createDirPath(io, dst) catch {};
    var dir = try std.Io.Dir.cwd().openDir(io, src, .{ .iterate = true });
    defer dir.close(io);

    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        const src_child = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ src, entry.name });
        defer gpa.free(src_child);
        const dst_child = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ dst, entry.name });
        defer gpa.free(dst_child);

        if (entry.kind == .directory) {
            try copyDir(io, gpa, src_child, dst_child);
        } else if (entry.kind == .file) {
            const bytes = try std.Io.Dir.cwd().readFileAlloc(io, src_child, gpa, .unlimited);
            defer gpa.free(bytes);
            try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = dst_child, .data = bytes });
        }
    }
}

/// Remove an installed package by its `name`: drop its `project.json` record
/// (store install) and/or delete its vendored `<project>/packages/<name>` dir.
/// The shared store copy is left intact for other projects.
fn cmdPackageRemove(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    _ = store_root;
    var removed = false;

    // 1. Remove from project.json `packages` (store install).
    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();
    if (cfg.removePackage(io, project_path, name) catch false) {
        removed = true;
        std.debug.print("[Turian] Removed '{s}' from {s}/project.json\n", .{ name, project_path });
    }

    // 2. Remove a vendored copy if present.
    var vbuf: [1024]u8 = undefined;
    const vendored = std.fmt.bufPrint(&vbuf, "{s}/packages/{s}", .{ project_path, name }) catch return error.PathTooLong;
    if (std.Io.Dir.cwd().openDir(io, vendored, .{})) |*d| {
        d.close(io);
        std.Io.Dir.cwd().deleteTree(io, vendored) catch |err| {
            std.debug.print("[Turian] Failed to remove vendored dir: {s}\n", .{@errorName(err)});
            return error.RemoveFailed;
        };
        removed = true;
        std.debug.print("[Turian] Removed vendored '{s}'\n", .{name});
    } else |_| {}

    if (!removed) {
        std.debug.print("[Turian] Package '{s}' is not installed.\n", .{name});
        return error.PackageNotFound;
    }
}

/// Update a package: re-install from its recorded source (re-fetch latest for
/// git, re-copy for local), picking up a new version into the store. With no
/// name, updates every store package. Vendored packages must be re-installed
/// manually (they have no recorded source).
fn cmdPackageUpdate(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();

    // Snapshot the (name, source, is_git) tuples up front — addPackage mutates
    // the slice as we go.
    var n: usize = 0;
    for (cfg.packages) |p| {
        if (name.len > 0 and !std.mem.eql(u8, p.name, name)) continue;
        if (p.source.len == 0) {
            std.debug.print("[Turian] '{s}' has no recorded source; skipping\n", .{p.name});
            continue;
        }
        const src = gpa.dupe(u8, p.source) catch continue;
        defer gpa.free(src);
        std.debug.print("[Turian] Updating '{s}' from {s}\n", .{ p.name, src });
        cmdPackageInstall(io, gpa, project_path, src, store_root, false) catch |err| {
            std.debug.print("[Turian] Update of '{s}' failed: {s}\n", .{ p.name, @errorName(err) });
            continue;
        };
        n += 1;
    }
    if (n == 0) std.debug.print("[Turian] Nothing to update.\n", .{});
}

/// List all installed packages.
fn cmdPackageList(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, store_root: []const u8) !void {
    var pm = editor.PackageManager.discover(io, gpa, project_path, editor.PackageManager.parseEngineVersion(build_options.version), store_root);
    defer pm.deinit();

    if (pm.packageCount() == 0) {
        std.debug.print("No packages installed in {s}\n", .{project_path});
        return;
    }

    std.debug.print("{d} package(s) installed in {s}:\n", .{ pm.packageCount(), project_path });
    for (pm.packages.items) |*pkg| {
        const types_str = formatTypes(pkg.manifest.types);
        std.debug.print("  {s}  v{s}  [{s}]\n", .{ pkg.manifest.name, pkg.manifest.version, types_str.slice() });
    }

    for (pm.diagnostics.items) |d| {
        std.debug.print("  {s}: {s}\n", .{ if (d.is_error) "error" else "warning", d.message });
    }
}

/// Show full manifest information for a named package.
fn cmdPackageInfo(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    var pm = editor.PackageManager.discover(io, gpa, project_path, editor.PackageManager.parseEngineVersion(build_options.version), store_root);
    defer pm.deinit();

    for (pm.packages.items) |*pkg| {
        if (!std.mem.eql(u8, pkg.manifest.name, name)) continue;
        const m = &pkg.manifest;
        const types_str = formatTypes(m.types);
        std.debug.print("Name:         {s}\n", .{m.name});
        std.debug.print("Version:      {s}\n", .{m.version});
        if (m.author.len > 0) std.debug.print("Author:       {s}\n", .{m.author});
        if (m.description.len > 0) std.debug.print("Description:  {s}\n", .{m.description});
        if (m.license.len > 0) std.debug.print("License:      {s}\n", .{m.license});
        if (m.engine_compat.len > 0) std.debug.print("Engine:       {s}\n", .{m.engine_compat});
        std.debug.print("Types:        {s}\n", .{types_str.slice()});
        std.debug.print("Location:     {s}\n", .{pkg.root});
        if (m.asset_dirs.len > 0) {
            std.debug.print("Asset dirs:   ", .{});
            for (m.asset_dirs, 0..) |d, i| {
                if (i > 0) std.debug.print(", ", .{});
                std.debug.print("{s}", .{d});
            }
            std.debug.print("\n", .{});
        }
        return;
    }

    std.debug.print("Package '{s}' is not installed.\n", .{name});
    return error.PackageNotFound;
}

pub const TypesStr = struct {
    buf: [64]u8,
    len: usize,
    pub fn slice(self: *const TypesStr) []const u8 {
        return self.buf[0..self.len];
    }
};

/// Format a slice of `PackageType` into a stack-allocated comma-separated string.
fn formatTypes(types: []const editor.PackageType) TypesStr {
    var r = TypesStr{ .buf = std.mem.zeroes([64]u8), .len = 0 };
    for (types, 0..) |t, i| {
        if (i > 0 and r.len < r.buf.len - 2) {
            r.buf[r.len] = ',';
            r.buf[r.len + 1] = ' ';
            r.len += 2;
        }
        const tag = @tagName(t);
        const remaining = r.buf.len - r.len;
        const copy_len = @min(tag.len, remaining);
        @memcpy(r.buf[r.len..][0..copy_len], tag[0..copy_len]);
        r.len += copy_len;
    }
    return r;
}

fn builtinCount(components: []const scanner.ComponentDef) usize {
    var n: usize = 0;
    for (components) |*c| if (c.is_builtin) {
        n += 1;
    };
    return n;
}
