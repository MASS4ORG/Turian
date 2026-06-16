/// Zig source-file generators — build.zig and main.zig for a standalone game.
/// Pure functions; no I/O, no GUI dependency.
const std = @import("std");
const ComponentDef = @import("Scanner.zig").ComponentDef;
const sdl3 = @import("platform/Sdl3Codegen.zig");

/// Window / runtime options baked into the generated game from the project's
/// `ProjectSettings` asset (issue #13). Defaults mirror `ProjectSettings`.
pub const RuntimeConfig = struct {
    title: []const u8 = "Turian Game",
    width: u32 = 1280,
    height: u32 = 720,
    vsync: bool = true,
    /// GUID of the scene the game boots into (loaded through the SceneManager,
    /// issue #22). Empty if no scene could be resolved.
    boot_scene_guid: []const u8 = "",
};

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
    /// Path to ktx2/src/root.zig. The module's `vendor/` C/C++ sources are
    /// derived from this (its grandparent directory).
    ktx2_root: []const u8,
    /// Path to gpu/src/root.zig (SDL3 window + device platform module).
    gpu_root: []const u8,
    /// Path to gpu/src/sdl3-c.h (translate-c root header for the SDL3 bindings).
    gpu_sdl3_c: []const u8,
    /// Path to render/root.zig (the shared GPU scene renderer).
    render_root: []const u8,
    /// SDL3 headers include directory (for translating the GPU bindings).
    sdl3_include: []const u8,
};

/// Return `path` with every backslash replaced by a forward-slash.
/// Zig string literals (and most tooling) accept `/` on all platforms, but
/// `\` in a generated .zig file is treated as an escape sequence and breaks
/// compilation on Windows.
pub fn normPath(a: std.mem.Allocator, path: []const u8) ![]const u8 {
    return std.mem.replaceOwned(u8, a, path, "\\", "/");
}

/// Normalize `path` and make it absolute (relative paths are taken under
/// `root`). Generated build.zig files run from the project's `.cache` dir, so
/// every embedded path must be absolute, not relative to the editor's cwd.
pub fn absUnder(a: std.mem.Allocator, root: []const u8, path: []const u8) ![]const u8 {
    if (path.len == 0 or std.fs.path.isAbsolute(path)) return normPath(a, path);
    const joined = try std.fmt.allocPrint(a, "{s}/{s}", .{ root, path });
    return normPath(a, joined);
}

/// Append `s` to `list`, escaping characters that would break a Zig `"..."`
/// string literal in the generated source (quotes, backslashes, control chars).
fn zigEscapeInto(a: std.mem.Allocator, list: *std.ArrayList(u8), s: []const u8) !void {
    for (s) |c| switch (c) {
        '"' => try list.appendSlice(a, "\\\""),
        '\\' => try list.appendSlice(a, "\\\\"),
        '\n' => try list.appendSlice(a, "\\n"),
        '\r' => try list.appendSlice(a, "\\r"),
        '\t' => try list.appendSlice(a, "\\t"),
        else => try list.append(a, c),
    };
}

pub fn sdl3LibPath(a: std.mem.Allocator, config: BuildConfig) []const u8 {
    if (config.sdl3_lib.len == 0) return "";
    if (std.fs.path.isAbsolute(config.sdl3_lib)) return config.sdl3_lib;
    return std.fmt.allocPrint(a, "{s}/{s}", .{ config.build_root, config.sdl3_lib }) catch config.sdl3_lib;
}

/// Emit the `ktx2` module (with its vendored Basis Universal transcoder + zstd
/// C/C++ sources) into a generated build.zig and import it into `engine_mod`.
/// The vendor paths are derived from `config.ktx2_root`'s module directory.
/// Shared by the game and play-mode generators.
pub fn appendKtx2Module(a: std.mem.Allocator, out: *std.ArrayList(u8), config: BuildConfig) !void {
    const d1 = std.fs.path.dirname(config.ktx2_root) orelse ".";
    const dir = std.fs.path.dirname(d1) orelse ".";
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const ktx2_mod = b.addModule(\"ktx2\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    ktx2_mod.link_libc = true;\n" ++
            "    ktx2_mod.link_libcpp = true;\n" ++
            "    ktx2_mod.addIncludePath(.{{ .cwd_relative = \"{s}/vendor\" }});\n" ++
            "    ktx2_mod.addIncludePath(.{{ .cwd_relative = \"{s}/vendor/basisu/transcoder\" }});\n" ++
            "    ktx2_mod.addIncludePath(.{{ .cwd_relative = \"{s}/vendor/basisu/zstd\" }});\n" ++
            "    const ktx2_flags = [_][]const u8{{ \"-std=c++17\", \"-fno-strict-aliasing\", \"-DBASISD_SUPPORT_KTX2=1\", \"-DBASISD_SUPPORT_KTX2_ZSTD=1\", \"-DBASISD_SUPPORT_DXT1=1\", \"-DBASISD_SUPPORT_DXT5A=1\", \"-DBASISD_SUPPORT_BC7_MODE5=1\", \"-DBASISD_SUPPORT_UASTC=1\", \"-DBASISD_SUPPORT_PVRTC1=0\", \"-DBASISD_SUPPORT_PVRTC2=0\", \"-DBASISD_SUPPORT_ATC=0\", \"-DBASISD_SUPPORT_ASTC=0\", \"-DBASISD_SUPPORT_FXT1=0\", \"-DBASISD_SUPPORT_ETC2_EAC_RG11=0\" }};\n" ++
            "    ktx2_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/vendor/ktx2_basis.cpp\" }}, .flags = &ktx2_flags }});\n" ++
            "    ktx2_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/vendor/basisu/transcoder/basisu_transcoder.cpp\" }}, .flags = &ktx2_flags }});\n" ++
            "    ktx2_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/vendor/basisu/zstd/zstddeclib.c\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            "    engine_mod.addImport(\"ktx2\", ktx2_mod);\n\n",
        .{ config.ktx2_root, dir, dir, dir, dir, dir, dir },
    ));
}

// ---------------------------------------------------------------------------
// build.zig generator

pub fn generateBuildZig(a: std.mem.Allocator, config: BuildConfig, src_files: []const []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);

    const sdl3_abs = sdl3LibPath(a, config);
    const sdl3_dir = if (sdl3_abs.len > 0)
        std.fs.path.dirname(sdl3_abs) orelse "."
    else
        ".";

    // GPU rendering needs SDL3 (lib + headers). Without it (e.g. headless CI),
    // the game falls back to the CPU software renderer.
    const use_gpu = sdl3_abs.len > 0 and config.sdl3_include.len > 0;

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

    try appendKtx2Module(a, &out, config);

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
            "    serde_mod.addImport(\"compat\", serde_compat_mod);\n" ++
            // engine imports serde (e.g. Material JSON load/save).
            "    engine_mod.addImport(\"serde\", serde_mod);\n\n",
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

    if (use_gpu) {
        // SDL3 C bindings (translate-c) → gpu platform module → render module.
        // The exe links SDL3 below, satisfying the GPU symbols these reference.
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    const sdl3_tc = b.addTranslateC(.{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "        .optimize = optimize,\n" ++
                "    }});\n" ++
                "    sdl3_tc.addIncludePath(.{{ .cwd_relative = \"{s}\" }});\n" ++
                "    const sdl3_c_mod = sdl3_tc.createModule();\n" ++
                "    const gpu_mod = b.addModule(\"gpu\", .{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "        .imports = &.{{.{{ .name = \"sdl3-c\", .module = sdl3_c_mod }}}},\n" ++
                "    }});\n" ++
                "    gpu_mod.link_libc = true;\n" ++
                "    const render_mod = b.addModule(\"render\", .{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "    }});\n" ++
                "    render_mod.addImport(\"engine\", engine_mod);\n" ++
                "    render_mod.addImport(\"gpu\", gpu_mod);\n\n",
            .{ config.gpu_sdl3_c, config.sdl3_include, config.gpu_root, config.render_root },
        ));
    }

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
    if (use_gpu) {
        try out.appendSlice(
            a,
            "                .{ .name = \"render\", .module = render_mod },\n" ++
                "                .{ .name = \"gpu\", .module = gpu_mod },\n",
        );
    }
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

pub fn generateMainZig(
    a: std.mem.Allocator,
    project_path: []const u8,
    src_files: []const []const u8,
    components: []const ComponentDef,
    component_count: usize,
    runtime: RuntimeConfig,
    use_gpu: bool,
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

    if (use_gpu) {
        // GPU renderer + its byte-source callbacks (one shared seam with the
        // editor; here backed by the packaged .oap by GUID).
        try out.appendSlice(
            a,
            "const gpu = @import(\"gpu\");\n" ++
                "const render = @import(\"render\");\n\n" ++
                "fn gpuSource(guid: []const u8) ?render.Bytes {\n" ++
                "    if (!g_assets_ready) return null;\n" ++
                "    const gid = (editor.Guid.parse(guid) catch return null).bytes;\n" ++
                "    const r = g_assets.readById(std.heap.page_allocator, gid) orelse return null;\n" ++
                "    return .{ .data = r.bytes, .owned = true };\n" ++
                "}\n\n",
        );
    }

    // Scene management runtime (issue #22): the SceneManager owns all scene node
    // storage. The loader resolves a scene asset GUID to nodes via the package.
    try out.appendSlice(
        a,
        "// Scene management (issue #22). The SceneManager owns every loaded scene's\n" ++
            "// nodes; the loader resolves a scene asset GUID → SceneNodes from the package.\n" ++
            "var g_scene_mgr: engine.SceneManager = undefined;\n" ++
            "const RENDER_CAP = engine.scene.MAX_OBJECTS * engine.SCENE_MANAGER_MAX_SCENES;\n" ++
            "var g_render_nodes: [RENDER_CAP]engine.SceneNode = undefined;\n\n" ++
            "fn sceneLoader(ctx: ?*anyopaque, id: []const u8, out: []engine.SceneNode, out_count: *usize) bool {\n" ++
            "    _ = ctx;\n" ++
            "    if (!g_assets_ready) return false;\n" ++
            "    const gid = (editor.Guid.parse(id) catch return false).bytes;\n" ++
            "    const r = g_assets.readById(std.heap.page_allocator, gid) orelse return false;\n" ++
            "    defer std.heap.page_allocator.free(r.bytes);\n" ++
            "    return editor.scene_io.loadSceneFromBytes(std.heap.page_allocator, r.bytes, out, out_count);\n" ++
            "}\n\n" ++
            "// Gather the nodes of every loaded scene into one buffer for the renderer,\n" ++
            "// which draws a single combined node list per frame (additive scenes share\n" ++
            "// one camera/light set).\n" ++
            "fn gatherRenderNodes() []engine.SceneNode {\n" ++
            "    var n: usize = 0;\n" ++
            "    var handles: [engine.SCENE_MANAGER_MAX_SCENES]engine.SceneHandle = undefined;\n" ++
            "    const loaded = g_scene_mgr.getLoadedScenes(&handles);\n" ++
            "    for (loaded) |h| {\n" ++
            "        for (g_scene_mgr.nodes(h)) |node| {\n" ++
            "            if (n >= RENDER_CAP) break;\n" ++
            "            g_render_nodes[n] = node;\n" ++
            "            n += 1;\n" ++
            "        }\n" ++
            "    }\n" ++
            "    return g_render_nodes[0..n];\n" ++
            "}\n\n",
    );

    // Platform layer (windowing + input + gamepad). Split into a dedicated
    // backend module so other platforms can be added later (issue #44 item 8).
    try out.appendSlice(a, sdl3.bindings);
    try out.appendSlice(a, sdl3.input);
    try out.appendSlice(a, sdl3.gamepad);

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
        // The boot scene GUID is loaded through the SceneManager at startup
        // (issue #22/#13). It is resolved from ProjectSettings.first_scene, or a
        // conventional fallback, at build time.
        const ss = std.fmt.bufPrint(&tmp, "const boot_scene_guid: []const u8 = \"{s}\";\n\n", .{runtime.boot_scene_guid}) catch return error.BufferTooSmall;
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
            "const MAX_LIVE = engine.scene.MAX_OBJECTS * engine.SCENE_MANAGER_MAX_SCENES;\n" ++
                "var g_live: [MAX_LIVE]LiveComponent = undefined;\n" ++
                "var g_live_transform: [MAX_LIVE]*engine.Transform = undefined;\n" ++
                "// The scene each live component belongs to, so we can destroy it when\n" ++
                "// its scene unloads and re-instantiate when a scene loads (issue #22).\n" ++
                "var g_live_handle: [MAX_LIVE]engine.SceneHandle = undefined;\n" ++
                "var g_live_count: usize = 0;\n" ++
                "// Per-scene-slot instantiation tracking (index → generation last seen).\n" ++
                "var g_slot_inst: [engine.SCENE_MANAGER_MAX_SCENES]bool = .{false} ** engine.SCENE_MANAGER_MAX_SCENES;\n" ++
                "var g_slot_inst_gen: [engine.SCENE_MANAGER_MAX_SCENES]u16 = .{0} ** engine.SCENE_MANAGER_MAX_SCENES;\n\n",
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
                "                        else if (@hasDecl(field.type, \"_turian_ref_kind\")) { var r: field.type = .{}; r.set(fv.refSlice()); @field(comp, field.name) = r; }\n" ++
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
        // Builds the per-update context (ADR 0001) from the engine-owned services.
        try out.appendSlice(
            a,
            "fn mkFrame(transform: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) engine.Frame {\n" ++
                "    return .{ .time = time, .input = &g_input, .transform = transform, .objects = objects, .services = &g_services };\n" ++
                "}\n\n",
        );
        // Lifecycle hooks accept either `hook(self)` or `hook(self, frame)` — the
        // context is injected into every hook, not just update (ADR 0001).
        for ([_][]const u8{ "awake", "enable", "start", "disable", "destroy" }) |hook| {
            const s = std.fmt.bufPrint(
                &tmp,
                "fn call_{s}(comp: *LiveComponent, transform: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) void {{\n" ++
                    "    switch (comp.*) {{ inline else => |*c| {{\n" ++
                    "        const CT = @TypeOf(c.*);\n" ++
                    "        if (comptime @hasDecl(CT, \"{s}\")) {{\n" ++
                    "            const P = @typeInfo(@TypeOf(CT.{s})).@\"fn\".params;\n" ++
                    "            if (P.len == 2 and P[1].type != null and P[1].type.? == engine.Frame) c.{s}(mkFrame(transform, objects, time)) else c.{s}();\n" ++
                    "        }}\n" ++
                    "    }} }}\n" ++
                    "}}\n\n",
                .{ hook, hook, hook, hook, hook },
            ) catch return error.BufferTooSmall;
            try out.appendSlice(a, s);
        }
        try out.appendSlice(
            a,
            "fn call_configure_input(comp: *LiveComponent, input: *engine.Input) void {\n" ++
                "    switch (comp.*) { inline else => |*c| if (comptime @hasDecl(@TypeOf(c.*), \"configureInput\")) c.configureInput(input) }\n" ++
                "}\n\n",
        );
        // update() dispatch: support `update(frame)` (ADR 0001), `update(transform, objects, time)`,
        // and the legacy `update(time)` form. Distinguish the Frame form by parameter type, not count.
        try out.appendSlice(
            a,
            "fn call_update(comp: *LiveComponent, transform: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) void {\n" ++
                "    switch (comp.*) { inline else => |*c| {\n" ++
                "        const CT = @TypeOf(c.*);\n" ++
                "        if (@hasDecl(CT, \"update\")) {\n" ++
                "            const P = @typeInfo(@TypeOf(CT.update)).@\"fn\".params;\n" ++
                "            if (P.len == 2 and P[1].type != null and P[1].type.? == engine.Frame) {\n" ++
                "                c.update(mkFrame(transform, objects, time));\n" ++
                "            } else if (P.len >= 4) {\n" ++
                "                c.update(transform, objects, time);\n" ++
                "            } else {\n" ++
                "                c.update(time);\n" ++
                "            }\n" ++
                "        }\n" ++
                "    } }\n" ++
                "}\n\n",
        );
        // Reconcile live components against the set of loaded scenes (issue #22):
        // destroy components whose scene unloaded, instantiate components for newly
        // loaded scenes. Persistent scenes stay loaded, so their components persist.
        try out.appendSlice(
            a,
            "fn slotInstantiated(h: engine.SceneHandle) bool {\n" ++
                "    return g_slot_inst[h.index] and g_slot_inst_gen[h.index] == h.generation;\n" ++
                "}\n\n" ++
                "fn syncLive() void {\n" ++
                "    const t0 = engine.Time{ .delta = 0, .elapsed = 0, .frame = 0 };\n" ++
                "    // 1. Destroy live components whose owning scene is gone.\n" ++
                "    var i: usize = 0;\n" ++
                "    while (i < g_live_count) {\n" ++
                "        if (!g_scene_mgr.isLoaded(g_live_handle[i])) {\n" ++
                "            call_disable(&g_live[i], g_live_transform[i], &.{}, t0);\n" ++
                "            call_destroy(&g_live[i], g_live_transform[i], &.{}, t0);\n" ++
                "            g_live_count -= 1;\n" ++
                "            g_live[i] = g_live[g_live_count];\n" ++
                "            g_live_transform[i] = g_live_transform[g_live_count];\n" ++
                "            g_live_handle[i] = g_live_handle[g_live_count];\n" ++
                "            continue;\n" ++
                "        }\n" ++
                "        i += 1;\n" ++
                "    }\n" ++
                "    // 2. Instantiate components for scenes not yet instantiated.\n" ++
                "    var handles: [engine.SCENE_MANAGER_MAX_SCENES]engine.SceneHandle = undefined;\n" ++
                "    const loaded = g_scene_mgr.getLoadedScenes(&handles);\n" ++
                "    for (loaded) |h| {\n" ++
                "        if (slotInstantiated(h)) continue;\n" ++
                "        g_slot_inst[h.index] = true;\n" ++
                "        g_slot_inst_gen[h.index] = h.generation;\n" ++
                "        const objs = g_scene_mgr.nodes(h);\n" ++
                "        for (objs) |*obj| {\n" ++
                "            if (!obj.active) continue;\n" ++
                "            for (obj.components[0..obj.component_count]) |*comp| {\n" ++
                "                if (comp.* != .user_script or g_live_count >= MAX_LIVE) continue;\n" ++
                "                if (instantiate(&comp.user_script)) |live| {\n" ++
                "                    g_live[g_live_count] = live;\n" ++
                "                    g_live_transform[g_live_count] = &obj.transform;\n" ++
                "                    g_live_handle[g_live_count] = h;\n" ++
                "                    call_configure_input(&g_live[g_live_count], &g_input);\n" ++
                "                    call_awake(&g_live[g_live_count], &obj.transform, objs, t0);\n" ++
                "                    call_enable(&g_live[g_live_count], &obj.transform, objs, t0);\n" ++
                "                    call_start(&g_live[g_live_count], &obj.transform, objs, t0);\n" ++
                "                    g_live_count += 1;\n" ++
                "                }\n" ++
                "            }\n" ++
                "        }\n" ++
                "    }\n" ++
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
            "    // Build the input action map from every InputActions asset in the package\n" ++
            "    // (data-driven bindings — issue #10). Runs before scripts so actions exist.\n" ++
            "    {\n" ++
            "        const ia_type: u8 = @intFromEnum(editor.AssetType.input_actions);\n" ++
            "        var _ia_i: usize = 0;\n" ++
            "        const _ia_n = g_assets.assetCount();\n" ++
            "        while (_ia_i < _ia_n) : (_ia_i += 1) {\n" ++
            "            const _ent = g_assets.assetEntryAt(_ia_i);\n" ++
            "            if (_ent.asset_type != ia_type) continue;\n" ++
            "            const _r = g_assets.readById(gpa, _ent.id) orelse continue;\n" ++
            "            defer gpa.free(_r.bytes);\n" ++
            "            const _ia = engine.assets.InputActions.loadFromBytes(gpa, _r.bytes) catch continue;\n" ++
            "            defer _ia.deinit(gpa);\n" ++
            "            _ia.applyTo(&g_input);\n" ++
            "            std.debug.print(\"[Turian] Loaded input actions ({d} action(s))\\n\", .{_ia.actions.len});\n" ++
            "        }\n" ++
            "    }\n\n" ++
            "    // Initialise the scene manager and boot into the configured first\n" ++
            "    // scene (issue #22). The manager owns all scene node storage and is\n" ++
            "    // published as a service so scripts can load/unload scenes.\n" ++
            "    g_scene_mgr = engine.SceneManager.init(gpa);\n" ++
            "    defer g_scene_mgr.deinit();\n" ++
            "    g_scene_mgr.setLoader(sceneLoader, null);\n" ++
            "    g_services.register(engine.SceneManager, &g_scene_mgr);\n" ++
            "    if (boot_scene_guid.len == 0) {\n" ++
            "        std.debug.print(\"[Turian] No boot scene configured\\n\", .{});\n" ++
            "        return;\n" ++
            "    }\n" ++
            "    _ = g_scene_mgr.loadScene(boot_scene_guid, .single) catch |err| {\n" ++
            "        std.debug.print(\"[Turian] Failed to load boot scene {s}: {any}\\n\", .{ boot_scene_guid, err });\n" ++
            "        return;\n" ++
            "    };\n" ++
            "    std.debug.print(\"[Turian] Booted scene {s}\\n\", .{boot_scene_guid});\n\n",
    );

    if (has_user) {
        // Instantiate live components for the boot scene (and any additively
        // loaded scenes) via the same reconciler used every frame.
        try out.appendSlice(a, "    syncLive();\n\n");
    }

    try out.appendSlice(
        a,
        "    if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_GAMEPAD)) {\n" ++
            "        std.debug.print(\"[Turian] SDL_Init failed\\n\", .{});\n" ++
            "        return;\n" ++
            "    }\n" ++
            "    defer SDL_Quit();\n\n",
    );

    // Window + renderer options come from ProjectSettings (issue #13):
    // title, resolution, and vsync are baked in from the project's settings asset.
    {
        var esc: std.ArrayList(u8) = .empty;
        try zigEscapeInto(a, &esc, runtime.title);
        if (use_gpu) {
            // GPU path: the gpu module owns the window + SDL3 GPU device; the
            // shared render module draws the scene into the swapchain.
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "    var win = gpu.Window.create(\"{s}\", .{{ .width = {d}, .height = {d}, .vsync = {s}, .shader_formats = .{{ .spirv = true }} }}) catch {{\n" ++
                    "        std.debug.print(\"[Turian] GPU window/device init failed\\n\", .{{}});\n" ++
                    "        return;\n" ++
                    "    }};\n" ++
                    "    defer win.deinit();\n" ++
                    "    render.init(win.device) catch {{\n" ++
                    "        std.debug.print(\"[Turian] render init failed\\n\", .{{}});\n" ++
                    "        return;\n" ++
                    "    }};\n" ++
                    "    defer render.deinit();\n" ++
                    "    render.setSources(gpuSource, gpuSource, gpuSource);\n\n",
                .{ esc.items, runtime.width, runtime.height, if (runtime.vsync) "true" else "false" },
            ));
        } else {
            const vsync_flag: u8 = if (runtime.vsync) 1 else 0;
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "    const window = SDL_CreateWindow(\"{s}\", {d}, {d}, 0) orelse {{\n" ++
                    "        std.debug.print(\"[Turian] SDL_CreateWindow failed\\n\", .{{}});\n" ++
                    "        return;\n" ++
                    "    }};\n" ++
                    "    defer SDL_DestroyWindow(window);\n\n" ++
                    "    const renderer = SDL_CreateRenderer(window, null) orelse {{\n" ++
                    "        std.debug.print(\"[Turian] SDL_CreateRenderer failed\\n\", .{{}});\n" ++
                    "        return;\n" ++
                    "    }};\n" ++
                    "    defer SDL_DestroyRenderer(renderer);\n" ++
                    "    _ = SDL_SetRenderVSync(renderer, {d});\n\n",
                .{ esc.items, runtime.width, runtime.height, vsync_flag },
            ));
            try out.appendSlice(
                a,
                "    const vp_w: c_int = engine.software_renderer.VP_W;\n" ++
                    "    const vp_h: c_int = engine.software_renderer.VP_H;\n" ++
                    "    const sdl_tex = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,\n" ++
                    "        SDL_TEXTUREACCESS_STREAMING, vp_w, vp_h) orelse {\n" ++
                    "        std.debug.print(\"[Turian] SDL_CreateTexture failed\\n\", .{});\n" ++
                    "        return;\n" ++
                    "    };\n" ++
                    "    defer SDL_DestroyTexture(sdl_tex);\n\n",
            );
        }
    }

    try out.appendSlice(
        a,
        "    var prev_ts = std.Io.Clock.awake.now(io);\n" ++
            "    var elapsed: f32 = 0;\n" ++
            "    var frame: u64 = 0;\n\n" ++
            "    main_loop: while (true) {\n" ++
            "        g_input.newFrame();\n" ++
            "        var ev: SDL_Event align(8) = undefined;\n" ++
            "        while (SDL_PollEvent(&ev)) {\n" ++
            "            switch (ev.type) {\n" ++
            "                SDL_EVENT_QUIT => break :main_loop,\n" ++
            "                SDL_EVENT_KEY_DOWN, SDL_EVENT_KEY_UP => {\n" ++
            "                    const ke: *const SDL_KeyboardEvent = @ptrCast(&ev);\n" ++
            "                    if (scancodeToKey(ke.scancode)) |k| g_input.setKey(k, ev.type == SDL_EVENT_KEY_DOWN);\n" ++
            "                },\n" ++
            "                SDL_EVENT_MOUSE_MOTION => {\n" ++
            "                    const me: *const SDL_MouseMotionEvent = @ptrCast(&ev);\n" ++
            "                    g_input.setMousePosition(me.x, me.y);\n" ++
            "                    g_input.addMouseMotion(me.xrel, me.yrel);\n" ++
            "                },\n" ++
            "                SDL_EVENT_MOUSE_BUTTON_DOWN, SDL_EVENT_MOUSE_BUTTON_UP => {\n" ++
            "                    const be: *const SDL_MouseButtonEvent = @ptrCast(&ev);\n" ++
            "                    if (sdlButtonToMouse(be.button)) |mb| g_input.setMouseButton(mb, ev.type == SDL_EVENT_MOUSE_BUTTON_DOWN);\n" ++
            "                },\n" ++
            "                SDL_EVENT_MOUSE_WHEEL => {\n" ++
            "                    const we: *const SDL_MouseWheelEvent = @ptrCast(&ev);\n" ++
            "                    g_input.addWheel(we.y);\n" ++
            "                },\n" ++
            "                SDL_EVENT_GAMEPAD_ADDED => {\n" ++
            "                    const ge: *const SDL_GamepadDeviceEvent = @ptrCast(&ev);\n" ++
            "                    _ = SDL_OpenGamepad(ge.which);\n" ++
            "                    g_input.gamepad_connected = true;\n" ++
            "                },\n" ++
            "                SDL_EVENT_GAMEPAD_BUTTON_DOWN, SDL_EVENT_GAMEPAD_BUTTON_UP => {\n" ++
            "                    const be: *const SDL_GamepadButtonEvent = @ptrCast(&ev);\n" ++
            "                    if (sdlPadButton(be.button)) |pb| g_input.setGamepadButton(pb, ev.type == SDL_EVENT_GAMEPAD_BUTTON_DOWN);\n" ++
            "                },\n" ++
            "                SDL_EVENT_GAMEPAD_AXIS_MOTION => {\n" ++
            "                    const ae: *const SDL_GamepadAxisEvent = @ptrCast(&ev);\n" ++
            "                    if (sdlPadAxis(ae.axis)) |pa| {\n" ++
            "                        const norm = @as(f32, @floatFromInt(ae.value)) / 32767.0;\n" ++
            "                        g_input.setGamepadAxis(pa, std.math.clamp(norm, -1.0, 1.0));\n" ++
            "                    }\n" ++
            "                },\n" ++
            "                else => {},\n" ++
            "            }\n" ++
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
                "        for (0..g_live_count) |_li| call_update(&g_live[_li], g_live_transform[_li], g_scene_mgr.nodes(g_live_handle[_li]), time);\n" ++
                "        // Apply any scene load/unload a script requested this frame, then\n" ++
                "        // reconcile live components against the new set of loaded scenes.\n" ++
                "        if (g_scene_mgr.flushRequests()) syncLive();\n\n",
        );
    }

    if (use_gpu) {
        try out.appendSlice(
            a,
            "        if (win.beginFrame()) |fr| {\n" ++
                "            render.renderScene(fr.cmd, fr.swapchain, fr.width, fr.height, gatherRenderNodes());\n" ++
                "            fr.submit();\n" ++
                "        }\n" ++
                "    }\n" ++
                "}\n",
        );
    } else {
        try out.appendSlice(
            a,
            "        engine.software_renderer.renderScene(io, gatherRenderNodes());\n" ++
                "        const pixels = engine.software_renderer.pixelsSlice();\n" ++
                "        _ = SDL_UpdateTexture(sdl_tex, null, pixels.ptr, vp_w * 4);\n" ++
                "        _ = SDL_RenderClear(renderer);\n" ++
                "        _ = SDL_RenderTexture(renderer, sdl_tex, null, null);\n" ++
                "        _ = SDL_RenderPresent(renderer);\n" ++
                "    }\n" ++
                "}\n",
        );
    }

    return try out.toOwnedSlice(a);
}
