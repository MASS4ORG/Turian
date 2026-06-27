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
        \\  debug       <subcommand>    Connect to a running Turian debug server
        \\  mcp                         Start an MCP server (stdio) backed by the debug server
        \\  docs        <subcommand>    Generate AI context or documentation
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
            // Ensure trailing slash.
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

fn builtinCount(components: []const scanner.ComponentDef) usize {
    var n: usize = 0;
    for (components) |*c| if (c.is_builtin) {
        n += 1;
    };
    return n;
}
