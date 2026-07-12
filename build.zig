const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const math3d_dep = b.dependency("math3d", .{ .target = target, .optimize = optimize });
    const math3d_mod = math3d_dep.module("math");

    // ── Guid module (standalone package at ../guid) ──────────────────────────
    const guid_dep = b.dependency("guid", .{ .target = target, .optimize = optimize });
    const guid_mod = guid_dep.module("guid");

    // ── Serde module (JSON / multi-format serialization) ─────────────────────
    const serde_dep = b.dependency("serde", .{ .target = target, .optimize = optimize });
    const serde_mod = serde_dep.module("serde");

    // ── Open Asset Package (standalone .oap reader/writer) ───────────────────
    const oap_dep = b.dependency("open_asset_package", .{ .target = target, .optimize = optimize });
    const oap_mod = oap_dep.module("open_asset_package");

    // ── KTX2 texture reader (+ vendored Basis Universal transcoder) ──────────
    const ktx2_dep = b.dependency("ktx2", .{ .target = target, .optimize = optimize });
    const ktx2_mod = ktx2_dep.module("ktx2");

    // ── CLI-only mode (skips studio/dvui/gpu/render; used for cross‑compilation) ─
    const cli_only = b.option(bool, "cli-only", "Build only turian-cli (no studio/dvui/gpu/render)") orelse false;

    // ── GPU platform module (SDL3 window + device), shared by studio + game ──
    const gpu_dep = if (!cli_only) b.dependency("gpu", .{ .target = target, .optimize = optimize }) else null;
    const gpu_mod = if (gpu_dep) |d| d.module("gpu") else null;

    // ── Engine module ────────────────────────────────────────────────────────
    const engine_mod = b.addModule("engine", .{
        .root_source_file = b.path("engine/root.zig"),
        .target = target,
    });
    engine_mod.link_libc = true;
    engine_mod.addIncludePath(b.path("engine/vendor"));
    engine_mod.addCSourceFile(.{ .file = b.path("engine/vendor/cgltf_wrap.c"), .flags = &.{"-std=c99"} });
    engine_mod.addImport("math", math3d_mod);
    engine_mod.addImport("open_asset_package", oap_mod);
    engine_mod.addImport("serde", serde_mod);
    engine_mod.addImport("ktx2", ktx2_mod);

    // ── Debug module (opt-in; never linked into game builds) ─────────────────
    // Hosts the JSON-RPC 2.0 TCP server + CLI client. Depends on engine for
    // scene/introspect types. Games must explicitly add this to link it.
    const debug_mod = b.addModule("debug", .{
        .root_source_file = b.path("subsystems/debug/root.zig"),
        .target = target,
    });
    debug_mod.addImport("engine", engine_mod);

    // ── MCP module (opt-in; stdio MCP server over debug protocol) ────────────
    const mcp_mod = b.addModule("mcp", .{
        .root_source_file = b.path("subsystems/mcp/root.zig"),
        .target = target,
    });
    mcp_mod.addImport("engine", engine_mod);
    mcp_mod.addImport("debug", debug_mod);

    // ── Editor module ────────────────────────────────────────────────────────
    const editor_mod = b.addModule("editor", .{
        .root_source_file = b.path("editor/root.zig"),
        .target = target,
    });
    editor_mod.addImport("engine", engine_mod);
    editor_mod.addImport("guid", guid_mod);
    editor_mod.addImport("serde", serde_mod);
    editor_mod.addImport("open_asset_package", oap_mod);

    // ── Render module ─────────────────────────────────────────────────────────
    // SDL3-GPU scene renderer shared by the studio viewport and the built game.
    // Depends on engine (scene/asset types) + gpu (SDL3 device/window) — never
    // pulled into the headless CLI.
    const render_mod = if (!cli_only) blk: {
        const m = b.addModule("render", .{
            .root_source_file = b.path("subsystems/render/root.zig"),
            .target = target,
        });
        m.addImport("engine", engine_mod);
        m.addImport("gpu", gpu_mod.?);
        break :blk m;
    } else null;

    // ── UI-render module ──────────────────────────────────────────────────────
    // Single node-tree -> dvui draw walk (#47 in-game GUI epic), shared by the
    // studio viewport overlay and the shipped game. Depends on engine (for
    // engine.UiDocument) + gui (dvui) — never pulled into the headless CLI.
    const ui_render_mod = if (!cli_only) blk: {
        const m = b.addModule("ui_render", .{
            .root_source_file = b.path("subsystems/ui_render/root.zig"),
            .target = target,
        });
        m.addImport("engine", engine_mod);
        break :blk m;
    } else null;

    // ── DVUI dependency ──────────────────────────────────────────────────────
    const dvui_dep = if (!cli_only) b.dependency("dvui", .{ .target = target, .optimize = optimize, .backend = .sdl3gpu }) else null;
    const dvui_mod = if (dvui_dep) |d| d.module("dvui_sdl3gpu") else null;

    // Second dvui backend variant: dvui's own headless `.testing` backend (no
    // GPU/window/X11) — lets `subsystems/ui_render/`'s tests actually run real
    // dvui frames (widget layout, ninepatch/image rendering, click simulation,
    // `dvui.testing.capturePng`) in `zig build test`, instead of relying on a
    // live Studio window that isn't reliably screenshot-able in CI/sandboxes.
    const dvui_testing_dep = if (!cli_only) b.dependency("dvui", .{ .target = target, .optimize = optimize, .backend = .testing }) else null;
    const dvui_testing_mod = if (dvui_testing_dep) |d| d.module("dvui_testing") else null;

    if (!cli_only) ui_render_mod.?.addImport("gui", dvui_mod.?);

    // ── Build options (paths baked in for game build system & CLI) ───────────
    const build_root = b.build_root.path orelse ".";

    // Version string parsed from build.zig.zon at build-graph construction time.
    // Hoisted here so the sdk step can embed it into turian-sdk.json as well.
    const version_str: []const u8 = blk: {
        const zon = @embedFile("build.zig.zon");
        const prefix = ".version = \"";
        const start = (std.mem.indexOf(u8, zon, prefix) orelse break :blk "0.0.0") + prefix.len;
        const end = std.mem.indexOfScalarPos(u8, zon, start, '"') orelse break :blk "0.0.0";
        break :blk zon[start..end];
    };

    const turian_opts = b.addOptions();
    turian_opts.addOption([]const u8, "api_zig_path", b.pathJoin(&.{ build_root, "engine", "api", "root.zig" }));
    turian_opts.addOption([]const u8, "engine_root_path", b.pathJoin(&.{ build_root, "engine", "root.zig" }));
    turian_opts.addOption([]const u8, "editor_root_path", b.pathJoin(&.{ build_root, "editor", "root.zig" }));
    turian_opts.addOption([]const u8, "cgltf_wrap_c_path", b.pathJoin(&.{ build_root, "engine", "vendor", "cgltf_wrap.c" }));
    turian_opts.addOption([]const u8, "vendor_include_path", b.pathJoin(&.{ build_root, "engine", "vendor" }));
    turian_opts.addOption([]const u8, "build_root_path", build_root);
    turian_opts.addOption([]const u8, "version", version_str);
    turian_opts.addOptionPath("math_root_path", math3d_dep.path("src/root.zig"));
    turian_opts.addOptionPath("guid_root_path", guid_dep.path("src/root.zig"));
    turian_opts.addOptionPath("oap_root_path", oap_dep.path("src/root.zig"));
    turian_opts.addOptionPath("serde_root_path", serde_dep.path("src/root.zig"));
    turian_opts.addOptionPath("serde_compat_root_path", serde_dep.path("src/compat_0_16.zig"));
    turian_opts.addOptionPath("ktx2_root_path", ktx2_dep.path("src/root.zig"));
    if (!cli_only) {
        turian_opts.addOptionPath("gpu_root_path", gpu_dep.?.path("src/root.zig"));
        turian_opts.addOptionPath("gpu_sdl3_c_path", gpu_dep.?.path("src/sdl3-c.h"));
    } else {
        turian_opts.addOption([]const u8, "gpu_root_path", "");
        turian_opts.addOption([]const u8, "gpu_sdl3_c_path", "");
    }
    turian_opts.addOption([]const u8, "render_root_path", b.pathJoin(&.{ build_root, "subsystems", "render", "root.zig" }));
    turian_opts.addOption([]const u8, "ui_render_root_path", b.pathJoin(&.{ build_root, "subsystems", "ui_render", "root.zig" }));

    // dvui's url+hash, so a generated game's build.zig.zon can declare the
    // SAME pinned dependency (C10: only games that reference a `.uidoc`
    // asset get this entry — see `GameBuild.regenerateBuildZon`). Read from
    // this project's own manifest so the two never drift apart.
    const dvui_url: []const u8 = "git+https://github.com/david-vanderson/dvui#405b282a2ef35c304ce61f33f840eeffa02ef3bd";
    const dvui_hash: []const u8 = "dvui-0.5.0-dev-AQFJmVem-QDeh_jOTFsL7_S-dKYGYbcOohEVdqyV7Eov";
    turian_opts.addOption([]const u8, "dvui_url", dvui_url);
    turian_opts.addOption([]const u8, "dvui_hash", dvui_hash);

    // SDL3 include tree, captured for the SDK step so it can ship the headers.
    var sdl3_include_tree: ?std.Build.LazyPath = null;
    if (!cli_only) {
        if (dvui_dep.?.builder.lazyDependency("sdl3", .{ .target = target, .optimize = optimize })) |sdl3_dep| {
            turian_opts.addOptionPath("sdl3_lib_path", sdl3_dep.artifact("SDL3").getEmittedBin());
            const inc = sdl3_dep.artifact("SDL3").getEmittedIncludeTree();
            turian_opts.addOptionPath("sdl3_include_path", inc);
            sdl3_include_tree = inc;
        } else {
            turian_opts.addOption([]const u8, "sdl3_lib_path", "");
            turian_opts.addOption([]const u8, "sdl3_include_path", "");
        }
    } else {
        turian_opts.addOption([]const u8, "sdl3_lib_path", "");
        turian_opts.addOption([]const u8, "sdl3_include_path", "");
    }

    // ── Studio (GUI editor) ──────────────────────────────────────────────────
    if (!cli_only) {
        const studio_exe = b.addExecutable(.{
            .name = "turian-studio",
            .root_module = b.createModule(.{
                .root_source_file = b.path("studio/Main.zig"),
                .target = target,
                .optimize = optimize,
                // Debug keeps symbols for local dev; anything else (Release*)
                // strips them — shipped binaries shouldn't carry debug info.
                // Most of the Debug/Release size gap is actually from Debug's
                // undefined-memory 0xaa safety fill forcing large fixed-size
                // scene-node buffers into real initialized .data bytes;
                // Release modes need neither that fill nor these symbols.
                .strip = optimize != .Debug,
                .imports = &.{
                    .{ .name = "gui", .module = dvui_mod.? },
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
                    .{ .name = "render", .module = render_mod.? },
                    .{ .name = "ui_render", .module = ui_render_mod.? },
                    .{ .name = "gpu", .module = gpu_mod.? },
                    .{ .name = "debug", .module = debug_mod },
                },
            }),
        });
        studio_exe.root_module.link_libc = true;
        studio_exe.root_module.addOptions("turian_build_options", turian_opts);
        b.installArtifact(studio_exe);

        const run_step = b.step("run", "Run Turian Studio");
        const run_cmd = b.addRunArtifact(studio_exe);
        run_step.dependOn(&run_cmd.step);
        run_cmd.step.dependOn(b.getInstallStep());
        if (b.args) |args| run_cmd.addArgs(args);
    }

    // ── Editor CLI (headless) ────────────────────────────────────────────────
    const cli_exe = b.addExecutable(.{
        .name = "turian-cli",
        .root_module = b.createModule(.{
            .root_source_file = b.path("editor/cli/Cli.zig"),
            .target = target,
            .optimize = optimize,
            .strip = optimize != .Debug,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
                .{ .name = "debug", .module = debug_mod },
                .{ .name = "mcp", .module = mcp_mod },
            },
        }),
    });
    cli_exe.root_module.link_libc = true;
    cli_exe.root_module.addOptions("turian_build_options", turian_opts);
    b.installArtifact(cli_exe);

    const cli_run_step = b.step("cli", "Run turian-cli");
    const cli_run_cmd = b.addRunArtifact(cli_exe);
    cli_run_step.dependOn(&cli_run_cmd.step);
    cli_run_cmd.step.dependOn(b.getInstallStep());
    if (b.args) |args| cli_run_cmd.addArgs(args);

    // ── Tests ────────────────────────────────────────────────────────────────
    // engine_mod doesn't link stb_image (studio provides it via dvui; the game
    // build compiles it per-game). The test module mirrors the render test
    // pattern: same source + deps, but adds the stb C file so the linker is
    // satisfied when refAllDecls pulls in ImageLoader.
    const engine_test_mod = b.createModule(.{
        .root_source_file = b.path("engine/root.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{
            .{ .name = "math", .module = math3d_mod },
            .{ .name = "open_asset_package", .module = oap_mod },
            .{ .name = "serde", .module = serde_mod },
            .{ .name = "ktx2", .module = ktx2_mod },
        },
    });
    engine_test_mod.link_libc = true;
    engine_test_mod.addIncludePath(b.path("engine/vendor"));
    engine_test_mod.addCSourceFile(.{ .file = b.path("engine/vendor/stb_image.c"), .flags = &.{"-std=c99"} });
    engine_test_mod.addCSourceFile(.{ .file = b.path("engine/vendor/cgltf_wrap.c"), .flags = &.{"-std=c99"} });
    const engine_tests = b.addTest(.{ .root_module = engine_test_mod });
    const editor_tests = b.addTest(.{ .root_module = editor_mod });

    const debug_test_mod = b.createModule(.{
        .root_source_file = b.path("subsystems/debug/root.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{
            .{ .name = "engine", .module = engine_test_mod },
        },
    });
    const debug_tests = b.addTest(.{ .root_module = debug_test_mod });

    const mcp_test_mod = b.createModule(.{
        .root_source_file = b.path("subsystems/mcp/root.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{
            .{ .name = "engine", .module = engine_test_mod },
            .{ .name = "debug", .module = debug_test_mod },
        },
    });
    const mcp_tests = b.addTest(.{ .root_module = mcp_test_mod });

    const studio_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("studio/services/EditorState.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
            },
        }),
    });
    studio_tests.root_module.addOptions("turian_build_options", turian_opts);

    // Pure CPU-side raster/audio math behind the asset preview system (#19/#25)
    // — no gui/render/gpu imports, so it's cheap to test standalone rather than
    // dragging in the full studio build graph.
    const preview_raster_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("studio/asset-browser/preview/PreviewRaster.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });

    // Editor-CPU frame phase timing (in-game GUI epic M0) — pure std, no gui
    // deps, cheap to test standalone like preview_raster_tests.
    const editor_frame_timing_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("studio/services/EditorFrameTiming.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });

    // asset_type -> editor draw-fn registry (issue #40, in-game GUI epic M0)
    // — only needs `editor` for AssetType, no gui/render/gpu graph.
    const editor_registry_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("studio/services/EditorRegistry.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "editor", .module = editor_mod },
            },
        }),
    });

    const test_step = b.step("test", "Run engine + editor + studio + render + debug + mcp tests");
    test_step.dependOn(&b.addRunArtifact(engine_tests).step);
    test_step.dependOn(&b.addRunArtifact(editor_tests).step);
    test_step.dependOn(&b.addRunArtifact(studio_tests).step);
    test_step.dependOn(&b.addRunArtifact(preview_raster_tests).step);
    test_step.dependOn(&b.addRunArtifact(editor_frame_timing_tests).step);
    test_step.dependOn(&b.addRunArtifact(editor_registry_tests).step);
    test_step.dependOn(&b.addRunArtifact(debug_tests).step);
    test_step.dependOn(&b.addRunArtifact(mcp_tests).step);

    // Focused steps for fast iteration on the debug/mcp stack.
    const test_debug_step = b.step("test-debug", "Run debug + mcp tests only");
    test_debug_step.dependOn(&b.addRunArtifact(debug_tests).step);
    test_debug_step.dependOn(&b.addRunArtifact(mcp_tests).step);

    if (!cli_only) {
        // The render module pulls in engine's ImageLoader (stb_image symbols). In
        // real builds stb comes from dvui (studio) or the game build; the standalone
        // test provides its own copy so it links.
        const render_test_mod = b.createModule(.{
            .root_source_file = b.path("subsystems/render/root.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "gpu", .module = gpu_mod.? },
            },
        });
        render_test_mod.link_libc = true;
        render_test_mod.addIncludePath(b.path("engine/vendor"));
        render_test_mod.addCSourceFile(.{ .file = b.path("engine/vendor/stb_image.c"), .flags = &.{"-std=c99"} });
        const render_tests = b.addTest(.{ .root_module = render_test_mod });
        test_step.dependOn(&b.addRunArtifact(render_tests).step);

        // Preview orbital-camera math (`studio/PreviewCamera.zig`) only needs
        // engine + render, not the full gui/dvui graph — test it standalone.
        const preview_camera_test_mod = b.createModule(.{
            .root_source_file = b.path("studio/asset-browser/preview/PreviewCamera.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "render", .module = render_test_mod },
            },
        });
        const preview_camera_tests = b.addTest(.{ .root_module = preview_camera_test_mod });
        test_step.dependOn(&b.addRunArtifact(preview_camera_tests).step);

        // UI node-tree -> dvui draw walk (#47 in-game GUI epic, M2). Tested
        // against dvui's own headless `.testing` backend so the composition
        // rule, stable IDs, and image/ninepatch styling run through real
        // dvui frames without a GPU/window.
        const ui_render_test_mod = b.createModule(.{
            .root_source_file = b.path("subsystems/ui_render/root.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "gui", .module = dvui_testing_mod.? },
            },
        });
        const ui_render_tests = b.addTest(.{ .root_module = ui_render_test_mod });
        test_step.dependOn(&b.addRunArtifact(ui_render_tests).step);
    }

    // ── CI step (test + release artifacts) ───────────────────────────────────
    // Pass -Dno-test=true when cross-compiling: the test runner can't execute
    // binaries built for a foreign target (e.g. aarch64-macos on x86_64-linux).
    const no_test = b.option(bool, "no-test", "Skip tests in the ci step (required for cross-compilation)") orelse false;
    const ci_step = b.step("ci", "Run all tests and build release artifacts");
    if (!no_test) ci_step.dependOn(test_step);
    if (!cli_only) {
        const rel_studio = b.addExecutable(.{
            .name = "turian-studio",
            .root_module = b.createModule(.{
                .root_source_file = b.path("studio/Main.zig"),
                .target = target,
                .optimize = .ReleaseFast,
                .strip = true,
                .imports = &.{
                    .{ .name = "gui", .module = dvui_mod.? },
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
                    .{ .name = "render", .module = render_mod.? },
                    .{ .name = "ui_render", .module = ui_render_mod.? },
                    .{ .name = "gpu", .module = gpu_mod.? },
                    .{ .name = "debug", .module = debug_mod },
                },
            }),
        });
        rel_studio.root_module.link_libc = true;
        rel_studio.root_module.addOptions("turian_build_options", turian_opts);
        ci_step.dependOn(&b.addInstallArtifact(rel_studio, .{}).step);
    }
    const rel_cli = b.addExecutable(.{
        .name = "turian-cli",
        .root_module = b.createModule(.{
            .root_source_file = b.path("editor/cli/Cli.zig"),
            .target = target,
            .optimize = .ReleaseFast,
            .strip = true,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
                .{ .name = "debug", .module = debug_mod },
                .{ .name = "mcp", .module = mcp_mod },
            },
        }),
    });
    rel_cli.root_module.link_libc = true;
    rel_cli.root_module.addOptions("turian_build_options", turian_opts);
    ci_step.dependOn(&b.addInstallArtifact(rel_cli, .{}).step);

    // ── Release tooling (tools/release.zig, always compiled for host) ────────
    // Three separate build steps share one compiled binary; each step injects
    // its subcommand as the first argument, then appends any `-- <user args>`.
    const release_tool = b.addExecutable(.{
        .name = "release",
        .root_module = b.createModule(.{
            .root_source_file = b.path("tools/Release.zig"),
            .target = b.graph.host,
            .optimize = .Debug,
        }),
    });

    // zig build release-check -- --commits .release-commits [--initial]
    {
        const step = b.step("release-check", "Check commits, bump version, update CHANGELOG, write .release-version");
        const run = b.addRunArtifact(release_tool);
        run.addArg("check");
        step.dependOn(&run.step);
        if (b.args) |args| run.addArgs(args);
    }

    // zig build release-package -- --platform <name> --version <v>
    {
        const step = b.step("release-package", "Package zig-out/bin binaries into dist/ for the given platform");
        const run = b.addRunArtifact(release_tool);
        run.addArg("package");
        step.dependOn(&run.step);
        if (b.args) |args| run.addArgs(args);
    }

    // zig build release-publish -- --provider gitlab
    {
        const step = b.step("release-publish", "Upload dist/ packages and create a release via the CI provider API");
        const run = b.addRunArtifact(release_tool);
        run.addArg("publish");
        step.dependOn(&run.step);
        if (b.args) |args| run.addArgs(args);
    }

    // ── SDK assembly (zig build sdk) ──────────────────────────────────────────
    // Produces zig-out/sdk/ — a self-contained, portable bundle that end-users
    // can use to build games without the full engine source checkout.  Requires
    // only Zig 0.16.0 on PATH; all engine/editor/dep Zig sources are shipped
    // inside.  Binaries sit at the bundle root for discoverability.
    //
    // Optimize level follows -Doptimize so the binaries match the dvui/SDL3
    // dependency build (releases pass -Doptimize=ReleaseFast).  Forcing
    // ReleaseFast here while the deps build at a different level produces a
    // sanitizer/link mismatch, so we deliberately reuse `optimize`.
    {
        const sdk_step = b.step("sdk", "Assemble a self-contained, portable SDK in zig-out/sdk/");

        // ── Binaries (at SDK root) ────────────────────────────────────────────
        const sdk_cli = b.addExecutable(.{
            .name = "turian-cli",
            .root_module = b.createModule(.{
                .root_source_file = b.path("editor/cli/Cli.zig"),
                .target = target,
                .optimize = optimize,
                .strip = optimize != .Debug,
                .imports = &.{
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
                    .{ .name = "debug", .module = debug_mod },
                    .{ .name = "mcp", .module = mcp_mod },
                },
            }),
        });
        sdk_cli.root_module.link_libc = true;
        sdk_cli.root_module.addOptions("turian_build_options", turian_opts);
        sdk_step.dependOn(&b.addInstallArtifact(sdk_cli, .{
            .dest_dir = .{ .override = .{ .custom = "sdk" } },
        }).step);

        if (!cli_only) {
            const sdk_studio = b.addExecutable(.{
                .name = "turian-studio",
                .root_module = b.createModule(.{
                    .root_source_file = b.path("studio/Main.zig"),
                    .target = target,
                    .optimize = optimize,
                    .strip = optimize != .Debug,
                    .imports = &.{
                        .{ .name = "gui", .module = dvui_mod.? },
                        .{ .name = "engine", .module = engine_mod },
                        .{ .name = "editor", .module = editor_mod },
                        .{ .name = "render", .module = render_mod.? },
                        .{ .name = "ui_render", .module = ui_render_mod.? },
                        .{ .name = "gpu", .module = gpu_mod.? },
                        .{ .name = "debug", .module = debug_mod },
                    },
                }),
            });
            sdk_studio.root_module.link_libc = true;
            sdk_studio.root_module.addOptions("turian_build_options", turian_opts);
            sdk_step.dependOn(&b.addInstallArtifact(sdk_studio, .{
                .dest_dir = .{ .override = .{ .custom = "sdk" } },
            }).step);

            // SDL3 runtime lib — game projects link it (Zig has no stable ABI,
            // so engine/editor ship as source; only the C lib is precompiled).
            if (dvui_dep.?.builder.lazyDependency("sdl3", .{
                .target = target,
                .optimize = optimize,
            })) |sdl3_dep| {
                sdk_step.dependOn(&b.addInstallArtifact(sdl3_dep.artifact("SDL3"), .{
                    .dest_dir = .{ .override = .{ .custom = "sdk/lib" } },
                }).step);
            }
        }

        // ── Engine / editor source trees (Zig + vendor C/headers, no docs) ────
        // No @embedFile in engine/editor, so dropping .md docs is safe.
        for ([_]struct { src: []const u8, dst: []const u8 }{
            .{ .src = "engine", .dst = "sdk/engine" },
            .{ .src = "editor", .dst = "sdk/editor" },
            // The render module ships as source (incl. its .spv shaders) so the
            // game build can compile the GPU renderer.
            .{ .src = "subsystems/render", .dst = "sdk/render" },
            // Debug module ships as source; game builds opt in explicitly.
            .{ .src = "subsystems/debug", .dst = "sdk/debug" },
            // MCP module ships as source; never linked into game builds.
            .{ .src = "subsystems/mcp", .dst = "sdk/mcp" },
        }) |d| {
            sdk_step.dependOn(&b.addInstallDirectory(.{
                .source_dir = b.path(d.src),
                .install_dir = .prefix,
                .install_subdir = d.dst,
                .exclude_extensions = &.{"md"},
            }).step);
        }

        // SDL3 headers — the game build translate-c's the gpu module's bindings
        // against these, so an SDK build is fully offline.
        if (sdl3_include_tree) |inc| {
            sdk_step.dependOn(&b.addInstallDirectory(.{
                .source_dir = inc,
                .install_dir = .prefix,
                .install_subdir = "sdk/sdl3-include",
            }).step);
        }

        // ── Dependency sources (src/ only — deps are self-contained Zig libs) ─
        // Each dep is installed to sdk/deps/<name>/src/ so SdkLayout.zig finds
        // src/root.zig at a fixed relative path; build.zig/docs/examples dropped.
        {
            const dep_list = [_]struct { dep: *std.Build.Dependency, name: []const u8 }{
                .{ .dep = math3d_dep, .name = "math3d" },
                .{ .dep = guid_dep, .name = "guid" },
                .{ .dep = serde_dep, .name = "serde" },
                .{ .dep = oap_dep, .name = "open_asset_package" },
                .{ .dep = ktx2_dep, .name = "ktx2" },
            };
            inline for (dep_list) |di| {
                sdk_step.dependOn(&b.addInstallDirectory(.{
                    .source_dir = di.dep.path("src"),
                    .install_dir = .prefix,
                    .install_subdir = b.fmt("sdk/deps/{s}/src", .{di.name}),
                    .exclude_extensions = &.{"md"},
                }).step);
            }
        }
        if (!cli_only) {
            sdk_step.dependOn(&b.addInstallDirectory(.{
                .source_dir = gpu_dep.?.path("src"),
                .install_dir = .prefix,
                .install_subdir = "sdk/deps/gpu/src",
                .exclude_extensions = &.{"md"},
            }).step);
        }

        // ktx2 also ships its vendored C/C++ transcoder sources (Basis Universal
        // + zstd), which the game/play build generators compile from disk.
        sdk_step.dependOn(&b.addInstallDirectory(.{
            .source_dir = ktx2_dep.path("vendor"),
            .install_dir = .prefix,
            .install_subdir = "sdk/deps/ktx2/vendor",
        }).step);

        // ── turian-sdk.json marker ────────────────────────────────────────────
        // Presence of this file (next to the binaries) tells SdkLayout.zig the
        // binary is running from an installed SDK bundle, not a dev checkout.
        const wf = b.addWriteFiles();
        const sdk_json = wf.add("turian-sdk.json", b.fmt(
            \\{{"layout_version":1,"version":"{s}","cli_only":{s}}}
        , .{ version_str, if (cli_only) "true" else "false" }));
        sdk_step.dependOn(&b.addInstallFile(sdk_json, "sdk/turian-sdk.json").step);
    }
}
