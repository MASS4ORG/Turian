/// Play-mode build system — generates and compiles a *play shared library*
/// from the current scene's user scripts so the studio can run the game's
/// update loop **in-process** inside the editor viewport.
///
/// This mirrors `GameBuild` (which produces a standalone executable), but the
/// product here is a `libturian_play.{so,dll,dylib}` exposing a small C ABI the
/// studio dlopen()s. The studio owns the window, rendering and the main loop;
/// the library only owns the scene node storage, the live user-script component
/// instances and the input snapshot, and steps them on demand.
///
/// Execution-model rationale (see docs/decisions/0002-play-mode.md): user
/// scripts are arbitrary `.zig` files compiled at project-build time, so they
/// cannot be linked into the studio binary up front. The reflection library
/// (UserReflection.zig) already proves the dlopen pattern works for metadata;
/// Play reuses it to actually *run* the scripts. The alternative — launching
/// the built game as a subprocess — cannot render inside the editor viewport,
/// which is the whole point of Play mode.
const std = @import("std");
const engine = @import("engine");
const ComponentDef = @import("../assets/Scanner.zig").ComponentDef;
const GameBuild = @import("GameBuild.zig");
const codegen = @import("GameCodegen.zig");
const PackageManager = @import("../package/PackageManager.zig").PackageManager;

pub const BuildConfig = GameBuild.BuildConfig;

const log = std.log.scoped(.play_build);

const lib_ext = if (@import("builtin").os.tag == .windows) ".dll" else if (@import("builtin").os.tag == .macos) ".dylib" else ".so";
const lib_prefix = if (@import("builtin").os.tag == .windows) "" else "lib";

/// Names of the C-ABI symbols the studio looks up after dlopen.
pub const symbols = struct {
    pub const start = "turianPlayStart";
    pub const update = "turianPlayUpdate";
    pub const stop = "turianPlayStop";
    pub const nodes_ptr = "turianPlayNodesPtr";
    pub const nodes_count = "turianPlayNodesCount";
    pub const new_frame = "turianPlayNewFrame";
    pub const set_key = "turianPlaySetKey";
    pub const set_mouse_button = "turianPlaySetMouseButton";
    pub const set_mouse_pos = "turianPlaySetMousePos";
    pub const add_mouse_motion = "turianPlayAddMouseMotion";
    pub const add_wheel = "turianPlayAddWheel";
    pub const load_input_actions = "turianPlayLoadInputActions";
    pub const register_prefab = "turianPlayRegisterPrefab";
    pub const load_ui_document = "turianPlayLoadUiDocument";
    pub const ui_runtime_ptr = "turianPlayUiRuntimePtr";
    pub const ui_events_ptr = "turianPlayUiEventsPtr";
    pub const game_event_registry_ptr = "turianPlayGameEventRegistryPtr";
    pub const quit_requested = "turianPlayQuitRequested";
    pub const diag_log_pump = "turianPlayDiagLogPump";
    pub const diag_log_ptr = "turianPlayDiagLogPtr";
    pub const diag_log_count = "turianPlayDiagLogCount";
};

fn normPath(a: std.mem.Allocator, path: []const u8) ![]const u8 {
    return std.mem.replaceOwned(u8, a, path, "\\", "/");
}

/// Compile a play shared library for `project_path` from the current set of
/// user-script component definitions. On success returns the absolute path to
/// the produced library (allocated in `a`); returns null on any failure.
///
/// Blocks until compilation finishes. POSIX-only (needs dlopen on the studio
/// side); returns null on Windows/WASI.
pub fn buildPlayLibrary(
    io: std.Io,
    a: std.mem.Allocator,
    project_path: []const u8,
    components: []const ComponentDef,
    component_count: usize,
    config: BuildConfig,
) ?[]const u8 {
    if (comptime @import("builtin").os.tag == .windows or @import("builtin").os.tag == .wasi) return null;

    return buildInner(io, a, project_path, components, component_count, config) catch |err| {
        log.err("Play build failed: {any}", .{err});
        return null;
    };
}

fn buildInner(
    io: std.Io,
    a: std.mem.Allocator,
    project_path: []const u8,
    components: []const ComponentDef,
    component_count: usize,
    config: BuildConfig,
) ![]const u8 {
    const cache_path = try std.fmt.allocPrint(a, "{s}/.cache/play", .{project_path});
    std.Io.Dir.cwd().createDirPath(io, cache_path) catch {};

    // Collect the unique source files of all user (non-builtin) components.
    var rel_files: [64][]const u8 = undefined;
    var abs_files: [64][]const u8 = undefined;
    var src_count: usize = 0;
    for (components[0..component_count]) |*def| {
        if (def.is_builtin) continue;
        const src = def.sourceFile();
        if (src.len == 0) continue;
        var found = false;
        for (rel_files[0..src_count]) |s| {
            if (std.mem.eql(u8, s, src)) found = true;
        }
        if (!found and src_count < rel_files.len) {
            rel_files[src_count] = src;
            abs_files[src_count] = if (std.fs.path.isAbsolute(src))
                try std.fmt.allocPrint(a, "{s}", .{src})
            else
                try std.fmt.allocPrint(a, "{s}/{s}", .{ config.build_root, src });
            src_count += 1;
        }
    }

    // Source modules from installed packages: wired into user scripts so
    // package code is importable from play-mode components too.
    var pm_pkgs = PackageManager.discover(io, a, project_path, PackageManager.parseEngineVersion(config.engine_version), config.package_store);
    defer pm_pkgs.deinit();
    const module_specs = blk: {
        const mods = pm_pkgs.sourceModules(a) catch break :blk &[_]codegen.ModuleSpec{};
        const specs = a.alloc(codegen.ModuleSpec, mods.len) catch break :blk &[_]codegen.ModuleSpec{};
        for (mods, 0..) |sm, i| {
            const abs = std.fmt.allocPrint(a, "{s}/{s}", .{ sm.root, sm.module.root }) catch sm.module.root;
            specs[i] = .{ .name = sm.module.name, .root_abs = codegen.normPath(a, abs) catch abs };
        }
        break :blk specs;
    };

    // Normalise every path embedded into generated Zig source (backslashes are
    // invalid escapes in a "..." literal).
    const gen_config = BuildConfig{
        .engine_root = try normPath(a, config.engine_root),
        .editor_root = try normPath(a, config.editor_root),
        .cgltf_wrap_c = try normPath(a, config.cgltf_wrap_c),
        .fbx_wrap_c = try normPath(a, config.fbx_wrap_c),
        .vendor_include = try normPath(a, config.vendor_include),
        .build_root = try normPath(a, config.build_root),
        .sdl3_lib = config.sdl3_lib,
        .math_root = try normPath(a, config.math_root),
        .guid_root = try normPath(a, config.guid_root),
        .oap_root = try normPath(a, config.oap_root),
        .serde_root = try normPath(a, config.serde_root),
        .serde_compat_root = try normPath(a, config.serde_compat_root),
        .ktx2_root = try normPath(a, config.ktx2_root),
        // The play library never renders (the editor draws play nodes), so these
        // are unused there — set to keep the shared BuildConfig complete.
        .gpu_root = config.gpu_root,
        .gpu_sdl3_c = config.gpu_sdl3_c,
        .render_root = config.render_root,
        .sdl3_include = config.sdl3_include,
        .extra_modules = module_specs,
    };
    for (0..src_count) |i| abs_files[i] = try normPath(a, abs_files[i]);

    const main_src = try generatePlayMainZig(a, rel_files[0..src_count], components, component_count);
    const main_path = try std.fmt.allocPrint(a, "{s}/play_main.zig", .{cache_path});
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = main_path, .data = main_src });

    const build_src = try generateBuildZig(a, gen_config, abs_files[0..src_count]);
    const build_zig_path = try std.fmt.allocPrint(a, "{s}/build.zig", .{cache_path});
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = build_zig_path, .data = build_src });

    log.info("Building play library...", .{});
    const argv = [_][]const u8{ "zig", "build", "-Doptimize=Debug" };
    try GameBuild.spawnAndWaitIn(io, a, &argv, cache_path);

    const lib_out = try std.fmt.allocPrint(
        a,
        "{s}/zig-out/lib/{s}turian_play{s}",
        .{ cache_path, lib_prefix, lib_ext },
    );
    log.info("Play library built: {s}", .{lib_out});
    return lib_out;
}

// ---------------------------------------------------------------------------
// build.zig generator — produces a shared library (no SDL: the software
// renderer is pure and rendering happens in the studio, not the library).

fn generateBuildZig(a: std.mem.Allocator, config: BuildConfig, src_files: []const []const u8) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);

    try out.appendSlice(a, "const std = @import(\"std\");\n\n");
    try out.appendSlice(a, "pub fn build(b: *std.Build) void {\n");
    try out.appendSlice(a, "    const target   = b.standardTargetOptions(.{});\n");
    try out.appendSlice(a, "    const optimize = b.standardOptimizeOption(.{});\n\n");

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const math_mod = b.addModule(\"math\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{config.math_root},
    ));

    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const oap_mod = b.addModule(\"open_asset_package\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{config.oap_root},
    ));

    // serde (+ its compat shim) — engine imports it (e.g. Material JSON load).
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
        "    const engine_mod = b.addModule(\"engine\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    engine_mod.link_libc = true;\n" ++
            "    engine_mod.addIncludePath(.{{ .cwd_relative = \"{s}\" }});\n" ++
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            "    engine_mod.addCSourceFile(.{{ .file = .{{ .cwd_relative = \"{s}/stb_image.c\" }}, .flags = &.{{\"-std=c99\"}} }});\n" ++
            "    engine_mod.addImport(\"math\", math_mod);\n" ++
            "    engine_mod.addImport(\"open_asset_package\", oap_mod);\n" ++
            "    engine_mod.addImport(\"serde\", serde_mod);\n\n",
        .{ config.engine_root, config.vendor_include, config.cgltf_wrap_c, config.fbx_wrap_c, config.vendor_include },
    ));

    try GameBuild.appendKtx2Module(a, &out, config);

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

    // Source modules from installed packages: each user script gets
    // the package module wired so it can `@import("pkgname")` in play mode.
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
        for (0..src_files.len) |si| {
            try out.appendSlice(a, try std.fmt.allocPrint(
                a,
                "    script_{d}_mod.addImport(\"{s}\", pkgmod_{d});\n",
                .{ si, m.name, mi },
            ));
        }
    }
    if (config.extra_modules.len > 0) try out.append(a, '\n');

    try out.appendSlice(
        a,
        "    const play_mod = b.createModule(.{\n" ++
            "        .root_source_file = b.path(\"play_main.zig\"),\n" ++
            "        .target = target,\n" ++
            "        .optimize = optimize,\n" ++
            "        .imports = &.{\n" ++
            "            .{ .name = \"engine\", .module = engine_mod },\n",
    );
    for (0..src_files.len) |i| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "            .{{ .name = \"script_{d}\", .module = script_{d}_mod }},\n",
            .{ i, i },
        ));
    }
    for (config.extra_modules, 0..) |m, mi| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "            .{{ .name = \"{s}\", .module = pkgmod_{d} }},\n",
            .{ m.name, mi },
        ));
    }
    try out.appendSlice(a, "        },\n    });\n");
    try out.appendSlice(a, "    play_mod.link_libc = true;\n");

    try out.appendSlice(
        a,
        "    const lib = b.addLibrary(.{\n" ++
            "        .name = \"turian_play\",\n" ++
            "        .root_module = play_mod,\n" ++
            "        .linkage = .dynamic,\n" ++
            "    });\n" ++
            "    b.installArtifact(lib);\n" ++
            "}\n",
    );

    return try out.toOwnedSlice(a);
}

// ---------------------------------------------------------------------------
// play_main.zig generator

fn generatePlayMainZig(
    a: std.mem.Allocator,
    src_files: []const []const u8,
    components: []const ComponentDef,
    component_count: usize,
) ![]u8 {
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
        "// Generated by Turian Studio Play mode - do not edit\n" ++
            "const std    = @import(\"std\");\n" ++
            "const engine = @import(\"engine\");\n\n" ++
            // Routes every `std.log.*` call from this library (play_main.zig
            // itself, and every user script — same compilation, same `engine`
            // instance) through the diagnostic ring: colored/timestamped on
            // this process's stderr immediately, and drained into the
            // studio's own ring each frame (`PlayMode.zig`'s `drainDiagLog`)
            // so it shows up in the Output panel too.
            "pub const std_options: std.Options = .{ .logFn = engine.DiagLog.logFn };\n\n" ++
            "const gpa = std.heap.page_allocator;\n\n" ++
            "// The library owns the live scene: node storage, the input snapshot and\n" ++
            "// (when there are user scripts) the instantiated component values.\n" ++
            "var g_nodes: [engine.scene.MAX_OBJECTS]engine.SceneNode = undefined;\n" ++
            "var g_node_count: usize = 0;\n" ++
            "var g_input: engine.Input = engine.Input.init();\n" ++
            "var g_services: engine.Services = engine.Services.init();\n" ++
            "var g_spawner: engine.Spawner = engine.Spawner.init(gpa);\n\n" ++
            // Snapshot buffer for `turianPlayDiagLogPump`/`Ptr`/`Count` below —
            // same "library owns POD, studio reads it by pointer" pattern as
            // `g_nodes`/`turianPlayNodesPtr`.
            "var g_diag_buf: [engine.DiagLog.capacity]engine.DiagLog.Entry = undefined;\n" ++
            "var g_diag_count: usize = 0;\n\n" ++
            // In-game GUI runtime: pure data, zero dvui import (D7) — the
            // library never renders (the studio draws, same as scene nodes), it
            // only owns the document instances scripts read/mutate via
            // `frame.uiDocument`/`frame.service(engine.ui.UiEvents)` and the
            // studio populates (`turianPlayLoadUiDocument`) / draws+dispatches
            // (`turianPlayUiRuntimePtr`/`turianPlayUiEventsPtr`).\n" ++
            "var g_ui_events: engine.ui.UiEvents = engine.ui.UiEvents.init();\n" ++
            "var g_ui_runtime: engine.ui.UiRuntime = engine.ui.UiRuntime.init();\n" ++
            // shared event-channel registry a button's `channel`
            // binding raises into (studio draws+dispatches via
            // `turianPlayGameEventRegistryPtr`, same pattern as the UI runtime
            // above); any script anywhere subscribes via `frame.gameEvent(ref)`.
            "var g_game_events: engine.GameEventRegistry = engine.GameEventRegistry.init();\n" ++
            // Unity's `Application.Quit()` analogue:
            // a script/UI handler calling `frame.service(engine.Application).quit()`
            // sets this; the studio (`PlayMode.pump`) polls it and Stops Play
            // mode — the same call a shipped game's generated `main` interprets
            // as "exit the process" (`GameCodegen`). One API, host decides what
            // "quit" means, no `#if EDITOR` branch needed in user scripts.\n" ++
            "var g_application: engine.Application = .{};\n\n",
    );

    for (0..src_files.len) |i| {
        const s = try std.fmt.bufPrint(&tmp, "const _uc{d}_mod = @import(\"script_{d}\");\n", .{ i, i });
        try out.appendSlice(a, s);
    }
    if (src_files.len > 0) try out.append(a, '\n');

    for (components[0..component_count]) |*def| {
        if (def.is_builtin) continue;
        const src = def.sourceFile();
        if (src.len == 0) continue;
        var src_idx: ?usize = null;
        for (src_files, 0..) |sf, i| {
            if (std.mem.eql(u8, sf, src)) src_idx = i;
        }
        const idx = src_idx orelse continue;
        const s = try std.fmt.bufPrint(&tmp, "pub const {s} = _uc{d}_mod.{s};\n", .{ def.typeName(), idx, def.typeName() });
        try out.appendSlice(a, s);
    }
    if (src_files.len > 0) try out.append(a, '\n');

    if (has_user) {
        try out.appendSlice(a, "const LiveComponent = union(enum) {\n");
        for (type_names[0..type_count]) |name| {
            const s = try std.fmt.bufPrint(&tmp, "    {s}: {s},\n", .{ name, name });
            try out.appendSlice(a, s);
        }
        try out.appendSlice(a, "};\n\n");

        try out.appendSlice(
            a,
            "var g_live: [engine.scene.MAX_OBJECTS]LiveComponent = undefined;\n" ++
                "var g_live_transform: [engine.scene.MAX_OBJECTS]*engine.Transform = undefined;\n" ++
                // Each live component remembers its owning node's GUID, so the live
                // set can be reconciled after runtime spawn/destroy (node storage
                // is compacted on destroy, which moves transforms).
                "var g_live_guid: [engine.scene.MAX_OBJECTS][36]u8 = undefined;\n" ++
                "var g_live_guid_len: [engine.scene.MAX_OBJECTS]usize = undefined;\n" ++
                "var g_live_count: usize = 0;\n\n",
        );

        // hydrate / instantiate — identical semantics to GameBuild's runtime.
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

        try out.appendSlice(
            a,
            "fn mkFrame(transform: *engine.Transform, objects: []engine.SceneNode, time: engine.Time) engine.Frame {\n" ++
                "    return .{ .time = time, .input = &g_input, .transform = transform, .objects = objects, .services = &g_services, .spawn = &g_spawner };\n" ++
                "}\n\n",
        );

        for ([_][]const u8{ "awake", "enable", "start", "disable", "destroy" }) |hook| {
            const s = try std.fmt.bufPrint(
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
            );
            try out.appendSlice(a, s);
        }

        try out.appendSlice(
            a,
            "fn call_configure_input(comp: *LiveComponent, input: *engine.Input) void {\n" ++
                "    switch (comp.*) { inline else => |*c| if (comptime @hasDecl(@TypeOf(c.*), \"configureInput\")) c.configureInput(input) }\n" ++
                "}\n\n",
        );

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

        // Runtime spawn/destroy support: instantiate live components
        // for a node, and reconcile the live set with the node buffer after a
        // spawn/destroy flush (re-pointing transforms by GUID, dropping comps for
        // destroyed nodes, and bringing freshly spawned nodes to life).
        try out.appendSlice(
            a,
            "fn liveAddNode(ni: usize) void {\n" ++
                "    const obj = &g_nodes[ni];\n" ++
                "    if (!obj.active) return;\n" ++
                "    const t0 = engine.Time{ .delta = 0, .elapsed = 0, .frame = 0 };\n" ++
                "    for (obj.components[0..obj.component_count]) |*comp| {\n" ++
                "        if (comp.* != .user_script or g_live_count >= g_live.len) continue;\n" ++
                "        if (instantiate(&comp.user_script)) |live| {\n" ++
                "            g_live[g_live_count] = live;\n" ++
                "            g_live_transform[g_live_count] = &obj.transform;\n" ++
                "            const gs = obj.guidSlice();\n" ++
                "            @memcpy(g_live_guid[g_live_count][0..gs.len], gs);\n" ++
                "            g_live_guid_len[g_live_count] = gs.len;\n" ++
                "            call_configure_input(&g_live[g_live_count], &g_input);\n" ++
                "            call_awake(&g_live[g_live_count], &obj.transform, g_nodes[0..g_node_count], t0);\n" ++
                "            call_enable(&g_live[g_live_count], &obj.transform, g_nodes[0..g_node_count], t0);\n" ++
                "            call_start(&g_live[g_live_count], &obj.transform, g_nodes[0..g_node_count], t0);\n" ++
                "            g_live_count += 1;\n" ++
                "        }\n" ++
                "    }\n" ++
                "}\n\n" ++
                "fn findNodeByGuid(guid: []const u8) ?usize {\n" ++
                "    for (g_nodes[0..g_node_count], 0..) |*o, i| {\n" ++
                "        if (std.mem.eql(u8, o.guidSlice(), guid)) return i;\n" ++
                "    }\n" ++
                "    return null;\n" ++
                "}\n\n" ++
                "fn liveHasNode(guid: []const u8) bool {\n" ++
                "    for (0..g_live_count) |i| {\n" ++
                "        if (std.mem.eql(u8, g_live_guid[i][0..g_live_guid_len[i]], guid)) return true;\n" ++
                "    }\n" ++
                "    return false;\n" ++
                "}\n\n" ++
                "fn reconcileLive() void {\n" ++
                "    var w: usize = 0;\n" ++
                "    for (0..g_live_count) |r| {\n" ++
                "        if (findNodeByGuid(g_live_guid[r][0..g_live_guid_len[r]])) |ni| {\n" ++
                "            g_live[w] = g_live[r];\n" ++
                "            g_live_guid[w] = g_live_guid[r];\n" ++
                "            g_live_guid_len[w] = g_live_guid_len[r];\n" ++
                "            g_live_transform[w] = &g_nodes[ni].transform;\n" ++
                "            w += 1;\n" ++
                "        }\n" ++
                "    }\n" ++
                "    g_live_count = w;\n" ++
                "    for (0..g_node_count) |ni| {\n" ++
                "        if (!g_nodes[ni].active) continue;\n" ++
                "        if (liveHasNode(g_nodes[ni].guidSlice())) continue;\n" ++
                "        liveAddNode(ni);\n" ++
                "    }\n" ++
                "}\n\n",
        );
    }

    // ── Exported C ABI ──────────────────────────────────────────────────────
    // The scene nodes are handed over directly from the studio: studio and
    // library share the exact same `engine.SceneNode` layout (same engine
    // source, same target), and SceneNode is self-contained POD (fixed buffers,
    // no heap pointers), so a memcpy fully transfers ownership of a copy. This
    // deliberately avoids running the JSON/serde parser inside the library.
    try out.appendSlice(
        a,
        "export fn turianPlayStart(nodes: [*]const engine.SceneNode, count: usize) callconv(.c) bool {\n" ++
            "    const n = @min(count, g_nodes.len);\n" ++
            "    @memcpy(g_nodes[0..n], nodes[0..n]);\n" ++
            "    g_node_count = n;\n" ++
            // Fresh instance each Play session: the library (and its globals)
            // can outlive one session when the studio reuses an unchanged
            // build (see `PlayMode.startFromNodes`'s hash check), so a prior
            // session's one-way `quit_requested` must not leak into the next.
            "    g_application = .{};\n" ++
            "    g_services.register(engine.ui.UiRuntime, &g_ui_runtime);\n" ++
            "    g_services.register(engine.ui.UiEvents, &g_ui_events);\n" ++
            "    g_services.register(engine.GameEventRegistry, &g_game_events);\n" ++
            "    g_services.register(engine.Application, &g_application);\n",
    );
    if (has_user) {
        try out.appendSlice(
            a,
            "    g_live_count = 0;\n" ++
                "    g_spawner.command_count = 0;\n" ++
                "    for (0..g_node_count) |ni| liveAddNode(ni);\n",
        );
    }
    try out.appendSlice(a, "    return true;\n}\n\n");

    try out.appendSlice(
        a,
        "export fn turianPlayUpdate(dt: f32, elapsed: f32, frame: u64) callconv(.c) void {\n",
    );
    if (has_user) {
        try out.appendSlice(
            a,
            "    const time = engine.Time{ .delta = dt, .elapsed = elapsed, .frame = frame };\n" ++
                "    for (0..g_live_count) |i| call_update(&g_live[i], g_live_transform[i], g_nodes[0..g_node_count], time);\n" ++
                // Apply any prefab spawns/destroys queued by scripts this frame,
                // then reconcile the live component set with the new node buffer.
                "    if (g_spawner.pending() > 0) {\n" ++
                "        if (g_spawner.flush(std.Io.Threaded.global_single_threaded.io(), &g_nodes, &g_node_count)) reconcileLive();\n" ++
                "    }\n",
        );
    } else {
        try out.appendSlice(a, "    _ = dt; _ = elapsed; _ = frame;\n");
    }
    try out.appendSlice(a, "}\n\n");

    try out.appendSlice(
        a,
        "export fn turianPlayStop() callconv(.c) void {\n",
    );
    if (has_user) {
        try out.appendSlice(
            a,
            "    const t0 = engine.Time{ .delta = 0, .elapsed = 0, .frame = 0 };\n" ++
                "    var i = g_live_count;\n" ++
                "    while (i > 0) {\n" ++
                "        i -= 1;\n" ++
                "        call_disable(&g_live[i], g_live_transform[i], g_nodes[0..g_node_count], t0);\n" ++
                "        call_destroy(&g_live[i], g_live_transform[i], g_nodes[0..g_node_count], t0);\n" ++
                "    }\n" ++
                "    g_live_count = 0;\n",
        );
    }
    try out.appendSlice(a, "    g_ui_runtime.deinitAll();\n");
    try out.appendSlice(a, "    g_node_count = 0;\n}\n\n");

    try out.appendSlice(
        a,
        "export fn turianPlayNodesPtr() callconv(.c) [*]engine.SceneNode {\n" ++
            "    return &g_nodes;\n" ++
            "}\n\n" ++
            "export fn turianPlayNodesCount() callconv(.c) usize {\n" ++
            "    return g_node_count;\n" ++
            "}\n\n" ++
            "export fn turianPlayNewFrame() callconv(.c) void {\n" ++
            "    g_input.newFrame();\n" ++
            "}\n\n" ++
            "export fn turianPlaySetKey(key: u16, down: bool) callconv(.c) void {\n" ++
            "    if (key == 0 or key > @intFromEnum(engine.Key.right_alt)) return;\n" ++
            "    g_input.setKey(@enumFromInt(key), down);\n" ++
            "}\n\n" ++
            "export fn turianPlaySetMouseButton(button: u8, down: bool) callconv(.c) void {\n" ++
            "    if (button > @intFromEnum(engine.MouseButton.x2)) return;\n" ++
            "    g_input.setMouseButton(@enumFromInt(button), down);\n" ++
            "}\n\n" ++
            "export fn turianPlaySetMousePos(x: f32, y: f32) callconv(.c) void {\n" ++
            "    g_input.setMousePosition(x, y);\n" ++
            "}\n\n" ++
            "export fn turianPlayAddMouseMotion(dx: f32, dy: f32) callconv(.c) void {\n" ++
            "    g_input.addMouseMotion(dx, dy);\n" ++
            "}\n\n" ++
            "export fn turianPlayAddWheel(delta: f32) callconv(.c) void {\n" ++
            "    g_input.addWheel(delta);\n" ++
            "}\n\n" ++
            "export fn turianPlayLoadInputActions(ptr: [*]const u8, len: usize) callconv(.c) void {\n" ++
            "    const ia = engine.assets.InputActions.loadFromBytes(gpa, ptr[0..len]) catch return;\n" ++
            "    defer ia.deinit(gpa);\n" ++
            "    ia.applyTo(&g_input);\n" ++
            "}\n\n" ++
            // Register a prefab's template nodes so scripts can Instantiate it by
            // GUID at runtime. Fed by the studio before play starts.
            "export fn turianPlayRegisterPrefab(guid_ptr: [*]const u8, guid_len: usize, nodes_ptr: [*]const engine.SceneNode, nodes_count: usize) callconv(.c) void {\n" ++
            "    g_spawner.registerPrefab(guid_ptr[0..guid_len], nodes_ptr[0..nodes_count]);\n" ++
            "}\n\n" ++
            // Loads one `.uidoc`'s bytes (read by the studio, which owns asset
            // access — the library never touches the asset database) into a
            // `UiInstance` owned by `node_guid`. The studio draws + dispatches
            // clicks against the *same* instance/events via the two pointer
            // accessors below (same process — dlopen, not a subprocess — so a
            // raw pointer to this pure-data struct is safe to alias from the
            // studio's own independently-compiled `engine.ui` code, exactly
            // like `turianPlayNodesPtr`'s `engine.SceneNode` pointer already is).\n" ++
            "export fn turianPlayLoadUiDocument(node_guid_ptr: [*]const u8, node_guid_len: usize, bytes_ptr: [*]const u8, bytes_len: usize) callconv(.c) void {\n" ++
            "    const node_guid = node_guid_ptr[0..node_guid_len];\n" ++
            "    if (g_ui_runtime.instanceFor(node_guid) != null) return;\n" ++
            "    var inst = engine.ui.UiInstance.load(gpa, bytes_ptr[0..bytes_len], &g_ui_events) catch return;\n" ++
            "    if (!g_ui_runtime.add(node_guid, inst)) inst.deinit();\n" ++
            "}\n\n" ++
            "export fn turianPlayUiRuntimePtr() callconv(.c) *engine.ui.UiRuntime {\n" ++
            "    return &g_ui_runtime;\n" ++
            "}\n\n" ++
            "export fn turianPlayUiEventsPtr() callconv(.c) *engine.ui.UiEvents {\n" ++
            "    return &g_ui_events;\n" ++
            "}\n\n" ++
            "export fn turianPlayGameEventRegistryPtr() callconv(.c) *engine.GameEventRegistry {\n" ++
            "    return &g_game_events;\n" ++
            "}\n\n" ++
            "export fn turianPlayQuitRequested() callconv(.c) bool {\n" ++
            "    return g_application.quit_requested;\n" ++
            "}\n\n" ++
            // Refreshes `g_diag_buf` from this library's own `DiagLog` ring
            // (fed by every `std.log.*` call in this compilation, per the
            // `std_options` above); the studio calls this once per frame,
            // then reads the result via the two accessors below.
            "export fn turianPlayDiagLogPump() callconv(.c) void {\n" ++
            "    g_diag_count = engine.DiagLog.snapshot(&g_diag_buf);\n" ++
            "}\n\n" ++
            "export fn turianPlayDiagLogPtr() callconv(.c) [*]engine.DiagLog.Entry {\n" ++
            "    return &g_diag_buf;\n" ++
            "}\n\n" ++
            "export fn turianPlayDiagLogCount() callconv(.c) usize {\n" ++
            "    return g_diag_count;\n" ++
            "}\n",
    );

    return try out.toOwnedSlice(a);
}
