/// Game build system — generates and compiles a standalone game executable
/// from the current scene and user scripts.  Pure logic with no GUI dependency.
const std = @import("std");
const ComponentDef = @import("Scanner.zig").ComponentDef;
const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;
const asset_importer = @import("AssetImporter.zig");
const asset_packager = @import("AssetPackager.zig");
const Progress = @import("Progress.zig").Progress;

/// Return `path` with every backslash replaced by a forward-slash.
/// Zig string literals (and most tooling) accept `/` on all platforms, but
/// `\` in a generated .zig file is treated as an escape sequence and breaks
/// compilation on Windows.
fn normPath(a: std.mem.Allocator, path: []const u8) ![]const u8 {
    return std.mem.replaceOwned(u8, a, path, "\\", "/");
}

/// Paths required to generate a game build. Most values come from
/// turian_build_options and can be overridden via environment variables.
pub const BuildConfig = struct {
    /// Path to engine/root.zig.
    engine_root: []const u8,
    /// Path to editor/root.zig.
    editor_root: []const u8,
    /// Path to engine/vendor/cgltf_wrap.c.
    cgltf_wrap_c: []const u8,
    /// Path to engine/vendor/ include directory.
    vendor_include: []const u8,
    /// Repository root path.
    build_root: []const u8,
    /// Path to libSDL3 (optional, for GPU renderer).
    sdl3_lib: []const u8,
    /// Path to math3d/src/root.zig.
    math_root: []const u8,
    /// Path to guid/src/root.zig.
    guid_root: []const u8,
    /// Path to open-asset-package/src/root.zig.
    oap_root: []const u8,
    /// Path to serde/src/root.zig.
    serde_root: []const u8,
    /// Path to serde/src/compat_0_16.zig (serde's internal compat shim).
    serde_compat_root: []const u8,
};

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

fn sdl3LibPath(a: std.mem.Allocator, config: BuildConfig) []const u8 {
    if (config.sdl3_lib.len == 0) return "";
    if (std.fs.path.isAbsolute(config.sdl3_lib)) return config.sdl3_lib;
    return std.fmt.allocPrint(a, "{s}/{s}", .{ config.build_root, config.sdl3_lib }) catch config.sdl3_lib;
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

    const scene_path = if (std.fs.path.isAbsolute(project_path))
        try std.fmt.allocPrint(a, "{s}/assets/scene-01.json", .{project_path})
    else
        try std.fmt.allocPrint(a, "{s}/{s}/assets/scene-01.json", .{ config.build_root, project_path });

    // Normalise all paths that will be embedded in generated Zig source files.
    // Backslashes inside string literals are invalid escape sequences.
    const gen_scene = try normPath(a, scene_path);
    const gen_config = BuildConfig{
        .engine_root = try normPath(a, config.engine_root),
        .editor_root = try normPath(a, config.editor_root),
        .cgltf_wrap_c = try normPath(a, config.cgltf_wrap_c),
        .vendor_include = try normPath(a, config.vendor_include),
        .build_root = try normPath(a, config.build_root),
        .sdl3_lib = try normPath(a, config.sdl3_lib),
        .math_root = try normPath(a, config.math_root),
        .guid_root = try normPath(a, config.guid_root),
        .oap_root = try normPath(a, config.oap_root),
        .serde_root = try normPath(a, config.serde_root),
        .serde_compat_root = try normPath(a, config.serde_compat_root),
    };
    for (0..src_count) |i| {
        abs_files[i] = try normPath(a, abs_files[i]);
    }

    progress.report(0.1, "Generating project");
    const gen_project = try normPath(a, project_path);
    const main_src = try generateMainZig(a, gen_project, gen_scene, rel_files[0..src_count], components, component_count);
    const main_path = try std.fmt.allocPrint(a, "{s}/main.zig", .{cache_path});
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = main_path, .data = main_src });

    const build_src = try generateBuildZig(a, gen_config, abs_files[0..src_count]);
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

// ---------------------------------------------------------------------------
// build.zig generator

fn generateBuildZig(a: std.mem.Allocator, config: BuildConfig, src_files: []const []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);

    const sdl3_abs = sdl3LibPath(a, config);
    const sdl3_dir = if (sdl3_abs.len > 0)
        std.fs.path.dirname(sdl3_abs) orelse "."
    else
        ".";

    try out.appendSlice(a, "const std = @import(\"std\");\n\n");
    try out.appendSlice(a, "pub fn build(b: *std.Build) void {\n");
    try out.appendSlice(a, "    const target   = b.standardTargetOptions(.{});\n");
    try out.appendSlice(a, "    const optimize  = b.standardOptimizeOption(.{});\n\n");

    // math module — engine/root.zig imports "math" so it must be declared first.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const math_mod = b.addModule(\"math\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{config.math_root},
    ));

    // Open Asset Package module — engine + editor import it for runtime asset loading.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const oap_mod = b.addModule(\"open_asset_package\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{config.oap_root},
    ));

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const engine_mod = b.addModule(\"engine\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    engine_mod.link_libc = true;\n" ++
            "    engine_mod.addIncludePath(.{{ .cwd_relative = \"{s}\" }});\n" ++
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            // stb_image implementation — the headless game has no other stb provider
            // (the studio gets it from dvui). Needed for material texture decoding.
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/stb_image.c\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            "    engine_mod.addImport(\"math\", math_mod);\n" ++
            "    engine_mod.addImport(\"open_asset_package\", oap_mod);\n\n",
        .{ config.engine_root, config.vendor_include, config.cgltf_wrap_c, config.vendor_include },
    ));

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const guid_mod = b.addModule(\"guid\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{config.guid_root},
    ));

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const serde_compat_mod = b.addModule(\"compat\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    const serde_mod = b.addModule(\"serde\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    serde_mod.addImport(\"compat\", serde_compat_mod);\n\n",
        .{ config.serde_compat_root, config.serde_root },
    ));

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const editor_mod = b.addModule(\"editor\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    editor_mod.addImport(\"engine\", engine_mod);\n" ++
            "    editor_mod.addImport(\"guid\", guid_mod);\n" ++
            "    editor_mod.addImport(\"serde\", serde_mod);\n" ++
            "    editor_mod.addImport(\"open_asset_package\", oap_mod);\n\n",
        .{config.editor_root},
    ));

    for (src_files, 0..) |sf, i| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    const script_{d}_mod = b.addModule(\"script_{d}\", .{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "    }});\n" ++
                "    script_{d}_mod.addImport(\"engine\", engine_mod);\n\n",
            .{ i, i, sf, i },
        ));
    }

    try out.appendSlice(
        a,
        "    const exe = b.addExecutable(.{\n" ++
            "        .name = \"game\",\n" ++
            "        .root_module = b.createModule(.{\n" ++
            "            .root_source_file = b.path(\"main.zig\"),\n" ++
            "            .target = target,\n" ++
            "            .optimize = optimize,\n" ++
            "            .imports = &.{\n" ++
            "                .{ .name = \"engine\", .module = engine_mod },\n" ++
            "                .{ .name = \"editor\", .module = editor_mod },\n",
    );
    for (0..src_files.len) |i| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "                .{{ .name = \"script_{d}\", .module = script_{d}_mod }},\n",
            .{ i, i },
        ));
    }
    try out.appendSlice(a, "            },\n        }),\n    });\n");
    try out.appendSlice(a, "    exe.root_module.link_libc = true;\n");

    if (sdl3_abs.len > 0) {
        // Link SDL3 (static library built by dvui).
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    exe.root_module.addLibraryPath(.{{ .cwd_relative = \"{s}\" }});\n" ++
                "    exe.root_module.linkSystemLibrary(\"SDL3\", .{{}});\n",
            .{sdl3_dir},
        ));
        // Windows system libraries required by SDL3 when statically linked.
        // dvui's own build wires these up automatically; our generated build must do it explicitly.
        try out.appendSlice(
            a,
            "    if (b.graph.host.result.os.tag == .windows) {\n" ++
                "        for (&[_][]const u8{\n" ++
                "            \"winmm\", \"ole32\", \"oleaut32\", \"setupapi\", \"imm32\",\n" ++
                "            \"version\", \"gdi32\", \"user32\", \"kernel32\", \"shell32\",\n" ++
                "            \"dinput8\", \"advapi32\", \"comdlg32\",\n" ++
                "        }) |lib| exe.root_module.linkSystemLibrary(lib, .{});\n" ++
                "    }\n",
        );
    }

    try out.appendSlice(
        a,
        "    b.installArtifact(exe);\n" ++
            "    // Ship the cooked asset package alongside the executable so the game\n" ++
            "    // can load it from its own directory — no source tree required.\n" ++
            "    b.installBinFile(\"game.oap\", \"game.oap\");\n" ++
            "    const run = b.step(\"run\", \"Run game\");\n" ++
            "    run.dependOn(&b.addRunArtifact(exe).step);\n" ++
            "}\n",
    );

    return try out.toOwnedSlice(a);
}

// ---------------------------------------------------------------------------
// main.zig generator

fn generateMainZig(
    a: std.mem.Allocator,
    project_path: []const u8,
    scene_path: []const u8,
    src_files: []const []const u8,
    components: []const ComponentDef,
    component_count: usize,
) ![]u8 {
    _ = project_path; // assets (incl. scene) now come from the package, not a path
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);
    var tmp: [512]u8 = undefined;

    var type_names: [64][]const u8 = undefined;
    var type_count: usize = 0;
    for (components[0..component_count]) |*def| {
        if (def.is_builtin) continue;
        if (def.sourceFile().len == 0) continue;
        const already = for (type_names[0..type_count]) |n| {
            if (std.mem.eql(u8, n, def.typeName())) break true;
        } else false;
        if (!already and type_count < type_names.len) {
            type_names[type_count] = def.typeName();
            type_count += 1;
        }
    }
    const has_user = type_count > 0;

    try out.appendSlice(
        a,
        "// Generated by Turian Studio - do not edit\n" ++
            "const std    = @import(\"std\");\n" ++
            "const engine = @import(\"engine\");\n" ++
            "const editor = @import(\"editor\");\n\n" ++
            "// All assets are loaded from the packaged game.oap — never the loose\n" ++
            "// filesystem. The software renderer pulls mesh bytes by GUID from here.\n" ++
            "var g_assets: engine.OapProvider = undefined;\n" ++
            "var g_assets_ready: bool = false;\n\n" ++
            "fn meshSource(guid: []const u8) ?engine.software_renderer.MeshSource {\n" ++
            "    if (!g_assets_ready) return null;\n" ++
            "    const gid = (editor.Guid.parse(guid) catch return null).bytes;\n" ++
            "    const r = g_assets.readById(std.heap.page_allocator, gid) orelse return null;\n" ++
            "    return .{ .bytes = r.bytes, .ext = r.ext, .owned = true };\n" ++
            "}\n\n" ++
            "fn materialSource(guid: []const u8) ?engine.software_renderer.MaterialSource {\n" ++
            "    if (!g_assets_ready) return null;\n" ++
            "    const gid = (editor.Guid.parse(guid) catch return null).bytes;\n" ++
            "    const r = g_assets.readById(std.heap.page_allocator, gid) orelse return null;\n" ++
            "    return .{ .bytes = r.bytes, .owned = true };\n" ++
            "}\n\n" ++
            "fn textureSource(guid: []const u8) ?engine.software_renderer.TextureSource {\n" ++
            "    if (!g_assets_ready) return null;\n" ++
            "    const gid = (editor.Guid.parse(guid) catch return null).bytes;\n" ++
            "    const r = g_assets.readById(std.heap.page_allocator, gid) orelse return null;\n" ++
            "    return .{ .bytes = r.bytes, .owned = true };\n" ++
            "}\n\n",
    );

    try out.appendSlice(
        a,
        "// SDL3 bindings\n" ++
            "const SDL_Window   = opaque {};\n" ++
            "const SDL_Renderer = opaque {};\n" ++
            "const SDL_Texture  = opaque {};\n" ++
            "const SDL_INIT_VIDEO: u32            = 0x00000020;\n" ++
            "const SDL_EVENT_QUIT: u32            = 0x100;\n" ++
            "const SDL_PIXELFORMAT_ABGR8888: u32  = 0x16762004;\n" ++
            "const SDL_TEXTUREACCESS_STREAMING: c_int = 1;\n" ++
            "const SDL_Event = extern struct { type: u32, padding: [124]u8 = undefined };\n" ++
            "extern fn SDL_Init(flags: u32) bool;\n" ++
            "extern fn SDL_CreateWindow(title: [*:0]const u8, w: c_int, h: c_int, flags: u64) ?*SDL_Window;\n" ++
            "extern fn SDL_DestroyWindow(w: *SDL_Window) void;\n" ++
            "extern fn SDL_PollEvent(e: *SDL_Event) bool;\n" ++
            "extern fn SDL_Quit() void;\n" ++
            "extern fn SDL_Delay(ms: u32) void;\n" ++
            "extern fn SDL_CreateRenderer(w: *SDL_Window, name: ?[*:0]const u8) ?*SDL_Renderer;\n" ++
            "extern fn SDL_DestroyRenderer(r: *SDL_Renderer) void;\n" ++
            "extern fn SDL_RenderClear(r: *SDL_Renderer) bool;\n" ++
            "extern fn SDL_RenderPresent(r: *SDL_Renderer) bool;\n" ++
            "extern fn SDL_CreateTexture(r: *SDL_Renderer, fmt: u32, access: c_int, w: c_int, h: c_int) ?*SDL_Texture;\n" ++
            "extern fn SDL_DestroyTexture(t: *SDL_Texture) void;\n" ++
            "extern fn SDL_UpdateTexture(t: *SDL_Texture, rect: ?*const anyopaque, pixels: *const anyopaque, pitch: c_int) bool;\n" ++
            "extern fn SDL_RenderTexture(r: *SDL_Renderer, t: *SDL_Texture, src: ?*const anyopaque, dst: ?*const anyopaque) bool;\n\n",
    );

    for (0..src_files.len) |i| {
        const s = std.fmt.bufPrint(&tmp, "const _uc{d}_mod = @import(\"script_{d}\");\n", .{ i, i }) catch return error.BufferTooSmall;
        try out.appendSlice(a, s);
    }
    if (src_files.len > 0) try out.append(a, '\n');

    for (components[0..component_count]) |*def| {
        if (def.is_builtin) continue;
        const src = def.sourceFile();
        if (src.len == 0) continue;
        var src_idx: ?usize = null;
        for (src_files, 0..) |sf, i| {
            if (std.mem.eql(u8, sf, src)) {
                src_idx = i;
                break;
            }
        }
        const idx = src_idx orelse continue;
        const s = std.fmt.bufPrint(&tmp, "pub const {s} = _uc{d}_mod.{s};\n", .{ def.typeName(), idx, def.typeName() }) catch return error.BufferTooSmall;
        try out.appendSlice(a, s);
    }
    if (src_files.len > 0) try out.append(a, '\n');

    {
        // Derive the scene's virtual path (project-relative, matching how the
        // packager keys assets). The packaged vpath is `assets/<...>`, so locate
        // the final `/assets/` segment in the absolute scene path.
        const sv = if (std.mem.lastIndexOf(u8, scene_path, "/assets/")) |idx|
            scene_path[idx + 1 ..]
        else
            scene_path;
        const ss = std.fmt.bufPrint(&tmp, "const scene_vpath:   []const u8 = \"{s}\";\n\n", .{sv}) catch return error.BufferTooSmall;
        try out.appendSlice(a, ss);
    }

    if (has_user) {
        try out.appendSlice(a, "const LiveComponent = union(enum) {\n");
        for (type_names[0..type_count]) |name| {
            const s = std.fmt.bufPrint(&tmp, "    {s}: {s},\n", .{ name, name }) catch return error.BufferTooSmall;
            try out.appendSlice(a, s);
        }
        try out.appendSlice(a, "};\n\n");
        try out.appendSlice(
            a,
            "const MAX_LIVE = engine.scene.MAX_OBJECTS * engine.scene.MAX_COMPONENTS;\n" ++
                "var g_live: [MAX_LIVE]LiveComponent = undefined;\n" ++
                "var g_live_transform: [MAX_LIVE]*engine.Transform = undefined;\n" ++
                "var g_live_count: usize = 0;\n\n",
        );
        try out.appendSlice(
            a,
            "fn hydrateComponent(comptime T: type, comp: *T, script: *const engine.UserScriptRef) void {\n" ++
                "    for (script.field_values[0..script.field_count]) |*fv| {\n" ++
                "        const fname = fv.nameSlice();\n" ++
                "        inline for (@typeInfo(T).@\"struct\".fields) |field| {\n" ++
                "            if (field.is_comptime) continue;\n" ++
                "            if (field.name[0] == '_') continue;\n" ++
                "            if (std.mem.eql(u8, fname, field.name)) {\n" ++
                "                switch (@typeInfo(field.type)) {\n" ++
                "                    .float => @field(comp, field.name) = @floatCast(fv.as_f32),\n" ++
                "                    .int   => @field(comp, field.name) = @intCast(fv.as_i32),\n" ++
                "                    .bool  => @field(comp, field.name) = fv.as_bool,\n" ++
                "                    .@\"struct\" => {\n" ++
                "                        if (field.type == engine.Vector3) @field(comp, field.name) = .{ .x = fv.as_vec3_x, .y = fv.as_vec3_y, .z = fv.as_vec3_z }\n" ++
                "                        else if (field.type == engine.GameObjectRef) { var r: engine.GameObjectRef = .{}; r.set(fv.refSlice()); @field(comp, field.name) = r; }\n" ++
                "                    },\n" ++
                "                    else => {},\n" ++
                "                }\n" ++
                "            }\n" ++
                "        }\n" ++
                "    }\n" ++
                "}\n\n" ++
                "fn instantiate(script: *const engine.UserScriptRef) ?LiveComponent {\n" ++
                "    const name = script.typeName();\n" ++
                "    inline for (std.meta.fields(LiveComponent)) |f| {\n" ++
                "        if (std.mem.eql(u8, name, f.name)) {\n" ++
                "            var inst: f.type = .{};\n" ++
                "            hydrateComponent(f.type, &inst, script);\n" ++
                "            return @unionInit(LiveComponent, f.name, inst);\n" ++
                "        }\n" ++
                "    }\n" ++
                "    return null;\n" ++
                "}\n\n",
        );
        for ([_][]const u8{ "awake", "enable", "start", "disable", "destroy" }) |hook| {
            const s = std.fmt.bufPrint(
                &tmp,
                "fn call_{s}(comp: *LiveComponent) void {{\n" ++
                    "    switch (comp.*) {{ inline else => |*c| if (comptime @hasDecl(@TypeOf(c.*), \"{s}\")) c.{s}() }}\n" ++
                    "}}\n\n",
                .{ hook, hook, hook },
            ) catch return error.BufferTooSmall;
            try out.appendSlice(a, s);
        }
        try out.appendSlice(
            a,
            "fn call_update(comp: *LiveComponent, transform: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) void {\n" ++
                "    switch (comp.*) { inline else => |*c| { const CT = @TypeOf(c.*); if (@hasDecl(CT, \"update\")) { const np = @typeInfo(@TypeOf(CT.update)).@\"fn\".params.len; if (np >= 4) c.update(transform, objects, time) else c.update(time); } } }\n" ++
                "}\n\n",
        );
    }

    try out.appendSlice(
        a,
        "pub fn main(init: std.process.Init) !void {\n" ++
            "    const io  = init.io;\n" ++
            "    const gpa = init.gpa;\n\n" ++
            "    // Open the cooked asset package shipped next to the executable.\n" ++
            "    // Every asset (meshes, textures, scenes) is served from here, so the\n" ++
            "    // game needs no source tree and no particular working directory.\n" ++
            "    const exe_dir = std.process.executableDirPathAlloc(io, gpa) catch {\n" ++
            "        std.debug.print(\"[Turian] Cannot locate executable directory\\n\", .{});\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer gpa.free(exe_dir);\n" ++
            "    const oap_path = std.fmt.allocPrint(gpa, \"{s}/game.oap\", .{exe_dir}) catch return;\n" ++
            "    defer gpa.free(oap_path);\n" ++
            "    g_assets = engine.OapProvider.initFromFile(io, gpa, oap_path) catch |err| {\n" ++
            "        std.debug.print(\"[Turian] Failed to open {s}: {any}\\n\", .{ oap_path, err });\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer g_assets.deinit();\n" ++
            "    g_assets_ready = true;\n" ++
            "    engine.software_renderer.setMeshSource(&meshSource);\n" ++
            "    engine.software_renderer.setMaterialSource(&materialSource);\n" ++
            "    engine.software_renderer.setTextureSource(&textureSource);\n\n" ++
            "    // Load the scene from the package.\n" ++
            "    const scene_bytes = g_assets.readByPath(gpa, scene_vpath) orelse {\n" ++
            "        std.debug.print(\"[Turian] Scene '{s}' not found in package\\n\", .{scene_vpath});\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer gpa.free(scene_bytes);\n" ++
            "    var objects: [engine.scene.MAX_OBJECTS]engine.SceneNode = undefined;\n" ++
            "    var object_count: usize = 0;\n" ++
            "    if (!editor.scene_io.loadSceneFromBytes(gpa, scene_bytes, &objects, &object_count)) {\n" ++
            "        std.debug.print(\"[Turian] No scene loaded\\n\", .{});\n" ++
            "        return;\n" ++
            "    }\n" ++
            "    std.debug.print(\"[Turian] Loaded {d} objects\\n\", .{object_count});\n\n",
    );

    if (has_user) {
        try out.appendSlice(
            a,
            "    for (objects[0..object_count]) |*obj| {\n" ++
                "        if (!obj.active) continue;\n" ++
                "        for (obj.components[0..obj.component_count]) |*comp| {\n" ++
                "            if (comp.* == .user_script) {\n" ++
                "                if (g_live_count < MAX_LIVE) {\n" ++
                "                    if (instantiate(&comp.user_script)) |live| {\n" ++
                "                        g_live[g_live_count] = live;\n" ++
                "                        g_live_transform[g_live_count] = &obj.transform;\n" ++
                "                        call_awake(&g_live[g_live_count]);\n" ++
                "                        call_enable(&g_live[g_live_count]);\n" ++
                "                        call_start(&g_live[g_live_count]);\n" ++
                "                        g_live_count += 1;\n" ++
                "                    }\n" ++
                "                }\n" ++
                "            }\n" ++
                "        }\n" ++
                "    }\n\n",
        );
    }

    try out.appendSlice(
        a,
        "    if (!SDL_Init(SDL_INIT_VIDEO)) {\n" ++
            "        std.debug.print(\"[Turian] SDL_Init failed\\n\", .{});\n" ++
            "        return;\n" ++
            "    }\n" ++
            "    defer SDL_Quit();\n\n" ++
            "    const window = SDL_CreateWindow(\"Turian Game\", 1280, 720, 0) orelse {\n" ++
            "        std.debug.print(\"[Turian] SDL_CreateWindow failed\\n\", .{});\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer SDL_DestroyWindow(window);\n\n" ++
            "    const renderer = SDL_CreateRenderer(window, null) orelse {\n" ++
            "        std.debug.print(\"[Turian] SDL_CreateRenderer failed\\n\", .{});\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer SDL_DestroyRenderer(renderer);\n\n" ++
            "    const vp_w: c_int = engine.software_renderer.VP_W;\n" ++
            "    const vp_h: c_int = engine.software_renderer.VP_H;\n" ++
            "    const sdl_tex = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,\n" ++
            "        SDL_TEXTUREACCESS_STREAMING, vp_w, vp_h) orelse {\n" ++
            "        std.debug.print(\"[Turian] SDL_CreateTexture failed\\n\", .{});\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    defer SDL_DestroyTexture(sdl_tex);\n\n" ++
            "    var prev_ts = std.Io.Clock.awake.now(io);\n" ++
            "    var elapsed: f32 = 0;\n" ++
            "    var frame: u64 = 0;\n\n" ++
            "    main_loop: while (true) {\n" ++
            "        var ev: SDL_Event align(8) = undefined;\n" ++
            "        while (SDL_PollEvent(&ev)) {\n" ++
            "            if (ev.type == SDL_EVENT_QUIT) break :main_loop;\n" ++
            "        }\n\n" ++
            "        const now_ts = std.Io.Clock.awake.now(io);\n" ++
            "        const dur    = prev_ts.durationTo(now_ts);\n" ++
            "        const delta: f32 = @as(f32, @floatFromInt(@as(i64, @intCast(dur.nanoseconds)))) / 1_000_000_000.0;\n" ++
            "        prev_ts = now_ts;\n" ++
            "        elapsed += delta;\n" ++
            "        frame   += 1;\n\n",
    );

    if (has_user) {
        try out.appendSlice(
            a,
            "        const time = engine.Time{ .delta = delta, .elapsed = elapsed, .frame = frame };\n" ++
                "        for (0..g_live_count) |_li| call_update(&g_live[_li], g_live_transform[_li], objects[0..object_count], time);\n\n",
        );
    }

    try out.appendSlice(
        a,
        "        engine.software_renderer.renderScene(io, objects[0..object_count]);\n" ++
            "        const pixels = engine.software_renderer.pixelsSlice();\n" ++
            "        _ = SDL_UpdateTexture(sdl_tex, null, pixels.ptr, vp_w * 4);\n" ++
            "        _ = SDL_RenderClear(renderer);\n" ++
            "        _ = SDL_RenderTexture(renderer, sdl_tex, null, null);\n" ++
            "        _ = SDL_RenderPresent(renderer);\n" ++
            "    }\n" ++
            "}\n",
    );

    return try out.toOwnedSlice(a);
}
