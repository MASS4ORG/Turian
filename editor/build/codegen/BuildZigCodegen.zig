const std = @import("std");
const Shared = @import("CodegenShared.zig");
const sanitizeZigId = @import("../../project/ProjectConfig.zig").sanitizeId;

pub const BuildConfig = Shared.BuildConfig;
pub const RuntimeConfig = Shared.RuntimeConfig;
pub const ModuleSpec = Shared.ModuleSpec;
pub const NativeLibSpec = Shared.NativeLibSpec;
pub const PluginSpec = Shared.PluginSpec;

pub fn sdl3LibPath(a: std.mem.Allocator, config: BuildConfig) []const u8 {
    return Shared.sdl3LibPath(a, config);
}

pub fn appendKtx2Module(a: std.mem.Allocator, out: *std.ArrayList(u8), config: BuildConfig) !void {
    return Shared.appendKtx2Module(a, out, config);
}

pub fn generateBuildZig(a: std.mem.Allocator, config: BuildConfig, src_files: []const []const u8, uses_ui: bool) ![]u8 {
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
            "    engine_mod.addImport(\"math\", math_mod);\n" ++
            "    engine_mod.addImport(\"open_asset_package\", oap_mod);\n\n",
        .{ config.engine_root, config.vendor_include, config.cgltf_wrap_c },
    ));
    if (!uses_ui) {
        // stb_image implementation — needed for material texture decoding.
        // Skipped when dvui is also linked (uses_ui): dvui vendors its own
        // stb_image implementation, and linking both into the same exe is a
        // duplicate-symbol error (mirrors Studio's own build.zig, which
        // always relies on dvui's copy instead of engine's).
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/stb_image.c\" }}, .flags = &.{{\"-std=c99\"}} }});\n\n",
            .{config.vendor_include},
        ));
    }

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

    if (uses_ui) {
        // In-game GUI (C10 pay-for-use): only emitted when the project
        // references a `.uidoc` asset, so a project that never uses
        // Guinevere ships with zero dvui linkage. `dvui`'s own build.zig
        // (resolved via b.dependency, matching Studio's exact wiring) owns
        // all backend/freetype/stb/tree-sitter selection — this only needs
        // to pick the `sdl3gpu` backend and re-share the SAME SDL3 the game
        // already links for its `gpu`/`render` modules.
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    const dvui_dep = b.dependency(\"dvui\", .{{ .target = target, .optimize = optimize, .backend = .sdl3gpu }});\n" ++
                "    const dvui_mod = dvui_dep.module(\"dvui_sdl3gpu\");\n" ++
                "    const ui_render_mod = b.addModule(\"ui_render\", .{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "    }});\n" ++
                "    ui_render_mod.addImport(\"engine\", engine_mod);\n" ++
                "    ui_render_mod.addImport(\"gui\", dvui_mod);\n\n",
            .{config.ui_render_root},
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

    // Source modules exported by installed packages. Each is a real
    // b.addModule importable from main.zig and from user scripts. Engine is
    // imported into each so package code can use the engine API.
    for (config.extra_modules, 0..) |m, mi| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    const pkgmod_{d} = b.addModule(\"{s}\", .{{\n" ++
                "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
                "        .target = target,\n" ++
                "    }});\n" ++
                "    pkgmod_{d}.addImport(\"engine\", engine_mod);\n",
            .{ mi, m.name, m.root_abs, mi },
        ));
        // Let user scripts import package modules by name.
        for (0..src_files.len) |si| {
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "    script_{d}_mod.addImport(\"{s}\", pkgmod_{d});\n",
                .{ si, m.name, mi },
            ));
        }
    }
    if (config.extra_modules.len > 0) try out.append(a, '\n');

    // Resolve third-party project dependencies through Zig's package manager
    //. Declared in project.json → generated build.zig.zon; reachable here
    // via b.dependency(). Source/native packages will extend this to
    // import modules / link artifacts; for now resolving proves the seam works.
    for (config.extra_deps) |dep_name| {
        var id_buf: [128]u8 = undefined;
        const dep_id = sanitizeZigId(dep_name, &id_buf);
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    const {s}_dep = b.dependency(\"{s}\", .{{ .target = target, .optimize = optimize }});\n" ++
                "    _ = {s}_dep; // resolved via Zig PM; imported by source/native packages\n",
            .{ dep_id, dep_id, dep_id },
        ));
    }
    if (config.extra_deps.len > 0) try out.append(a, '\n');

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
    if (uses_ui) {
        try out.appendSlice(
            a,
            "                .{ .name = \"gui\", .module = dvui_mod },\n" ++
                "                .{ .name = \"ui_render\", .module = ui_render_mod },\n",
        );
    }
    for (0..src_files.len) |i| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "                .{{ .name = \"script_{d}\", .module = script_{d}_mod }},\n",
            .{ i, i },
        ));
    }
    for (config.extra_modules, 0..) |m, mi| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "                .{{ .name = \"{s}\", .module = pkgmod_{d} }},\n",
            .{ m.name, mi },
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

    // Native libraries from installed packages. The `{target}` token in a
    // lib path template is replaced with the build's Zig target triple so the
    // platform-correct binary is selected. Static libs are added as object
    // files; shared libs are linked by name from their directory.
    for (config.extra_native, 0..) |nl, i| {
        const include_abs = if (nl.include.len > 0)
            try std.fmt.allocPrint(a, "{s}/{s}", .{ nl.pkg_root, nl.include })
        else
            "";
        const is_static = !std.mem.eql(u8, nl.kind, "shared");
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    {{\n" ++
                "        const _nt{d} = target.result.zigTriple(b.allocator) catch \"\";\n" ++
                "        const _nrel{d} = std.mem.replaceOwned(u8, b.allocator, \"{s}\", \"{{target}}\", _nt{d}) catch \"{s}\";\n" ++
                "        const _nabs{d} = b.pathJoin(&.{{ \"{s}\", _nrel{d} }});\n",
            .{ i, i, nl.lib_template, i, nl.lib_template, i, nl.pkg_root, i },
        ));
        if (is_static) {
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "        exe.root_module.addObjectFile(.{{ .cwd_relative = _nabs{d} }});\n",
                .{i},
            ));
        } else {
            // Shared: link by name from the resolved file's directory.
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "        const _ndir{d} = std.fs.path.dirname(_nabs{d}) orelse \".\";\n" ++
                    "        exe.root_module.addLibraryPath(.{{ .cwd_relative = _ndir{d} }});\n" ++
                    "        exe.root_module.linkSystemLibrary(\"{s}\", .{{}});\n",
                .{ i, i, i, nl.name },
            ));
        }
        if (include_abs.len > 0) {
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "        exe.root_module.addIncludePath(.{{ .cwd_relative = \"{s}\" }});\n",
                .{include_abs},
            ));
        }
        try out.appendSlice(a, "        exe.root_module.link_libc = true;\n    }\n");
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

// Tests for generateBuildZig

test "generateBuildZig emits native lib linking" {
    // Arena, not `testing.allocator` — every real caller (GameBuild.zig's CLI
    // path, PlayMode.zig's loadLibrary) wraps this generator in an arena and
    // frees it all at once; matching that here avoids flagging the internal
    // `allocPrint` temporaries (never individually freed, by design) as leaks.
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();
    const native = [_]NativeLibSpec{.{
        .pkg_root = "/store/com.acme.physx/1.0.0",
        .name = "physx",
        .kind = "static",
        .lib_template = "lib/{target}/libphysx.a",
        .include = "include",
    }};
    const cfg = BuildConfig{
        .engine_root = "/e/root.zig",
        .editor_root = "/ed/root.zig",
        .cgltf_wrap_c = "/v/cgltf.c",
        .vendor_include = "/v",
        .build_root = "/r",
        .sdl3_lib = "",
        .math_root = "/m/root.zig",
        .guid_root = "/g/root.zig",
        .oap_root = "/o/root.zig",
        .serde_root = "/s/root.zig",
        .serde_compat_root = "/s/compat.zig",
        .ktx2_root = "/k/src/root.zig",
        .gpu_root = "",
        .gpu_sdl3_c = "",
        .render_root = "",
        .sdl3_include = "",
        .extra_native = &native,
    };
    const out = try generateBuildZig(a, cfg, &.{}, false);
    // Static lib is added as an object file, the target token is substituted,
    // and the include path is wired.
    try std.testing.expect(std.mem.indexOf(u8, out, "addObjectFile") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "replaceOwned(u8, b.allocator, \"lib/{target}/libphysx.a\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "/store/com.acme.physx/1.0.0") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "addIncludePath(.{ .cwd_relative = \"/store/com.acme.physx/1.0.0/include\" })") != null);
}

test "generateBuildZig emits source package modules and wires them into scripts" {
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();
    const mods = [_]ModuleSpec{.{
        .name = "platformer",
        .root_abs = "/store/com.acme.platformer/1.0.0/src/root.zig",
    }};
    const cfg = BuildConfig{
        .engine_root = "/e/root.zig",
        .editor_root = "/ed/root.zig",
        .cgltf_wrap_c = "/v/cgltf.c",
        .vendor_include = "/v",
        .build_root = "/r",
        .sdl3_lib = "",
        .math_root = "/m/root.zig",
        .guid_root = "/g/root.zig",
        .oap_root = "/o/root.zig",
        .serde_root = "/s/root.zig",
        .serde_compat_root = "/s/compat.zig",
        .ktx2_root = "/k/src/root.zig",
        .gpu_root = "",
        .gpu_sdl3_c = "",
        .render_root = "",
        .sdl3_include = "",
        .extra_modules = &mods,
    };
    const src = [_][]const u8{"/proj/src/Player.zig"};
    const out = try generateBuildZig(a, cfg, &src, false);
    // Package module declared and imported into engine.
    try std.testing.expect(std.mem.indexOf(u8, out, "pkgmod_0") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "\"platformer\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "/store/com.acme.platformer/1.0.0/src/root.zig") != null);
    // Package module imported into user script.
    try std.testing.expect(std.mem.indexOf(u8, out, "script_0_mod.addImport(\"platformer\", pkgmod_0)") != null);
    // Package module available in the exe imports.
    try std.testing.expect(std.mem.indexOf(u8, out, ".{ .name = \"platformer\", .module = pkgmod_0 }") != null);
}

test "generateBuildZig emits shared lib link by name" {
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();
    const native = [_]NativeLibSpec{.{
        .pkg_root = "/store/com.acme.snd/2.0.0",
        .name = "snd",
        .kind = "shared",
        .lib_template = "lib/{target}/libsnd.so",
        .include = "",
    }};
    const cfg = BuildConfig{
        .engine_root = "/e/root.zig",
        .editor_root = "/ed/root.zig",
        .cgltf_wrap_c = "/v/cgltf.c",
        .vendor_include = "/v",
        .build_root = "/r",
        .sdl3_lib = "",
        .math_root = "/m/root.zig",
        .guid_root = "/g/root.zig",
        .oap_root = "/o/root.zig",
        .serde_root = "/s/root.zig",
        .serde_compat_root = "/s/compat.zig",
        .ktx2_root = "/k/src/root.zig",
        .gpu_root = "",
        .gpu_sdl3_c = "",
        .render_root = "",
        .sdl3_include = "",
        .extra_native = &native,
    };
    const out = try generateBuildZig(a, cfg, &.{}, false);
    try std.testing.expect(std.mem.indexOf(u8, out, "linkSystemLibrary(\"snd\", .{})") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "addLibraryPath") != null);
}
