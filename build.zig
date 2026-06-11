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

    // ── Editor module ────────────────────────────────────────────────────────
    const editor_mod = b.addModule("editor", .{
        .root_source_file = b.path("editor/root.zig"),
        .target = target,
    });
    editor_mod.addImport("engine", engine_mod);
    editor_mod.addImport("guid", guid_mod);
    editor_mod.addImport("serde", serde_mod);
    editor_mod.addImport("open_asset_package", oap_mod);

    // ── CLI-only mode (skips studio/dvui; used for cross-compilation) ────────
    const cli_only = b.option(bool, "cli-only", "Build only turian-cli (no studio/dvui)") orelse false;

    // ── DVUI dependency ──────────────────────────────────────────────────────
    const dvui_dep = if (!cli_only) b.dependency("dvui", .{ .target = target, .optimize = optimize, .backend = .sdl3gpu }) else null;
    const dvui_mod = if (dvui_dep) |d| d.module("dvui_sdl3gpu") else null;

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
    turian_opts.addOption([]const u8, "reflection_zig_path", b.pathJoin(&.{ build_root, "engine", "Reflection.zig" }));
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

    if (!cli_only) {
        if (dvui_dep.?.builder.lazyDependency("sdl3", .{ .target = target, .optimize = optimize })) |sdl3_dep|
            turian_opts.addOptionPath("sdl3_lib_path", sdl3_dep.artifact("SDL3").getEmittedBin())
        else
            turian_opts.addOption([]const u8, "sdl3_lib_path", "");
    } else {
        turian_opts.addOption([]const u8, "sdl3_lib_path", "");
    }

    // ── Studio (GUI editor) ──────────────────────────────────────────────────
    if (!cli_only) {
        const studio_exe = b.addExecutable(.{
            .name = "turian-studio",
            .root_module = b.createModule(.{
                .root_source_file = b.path("studio/Main.zig"),
                .target = target,
                .optimize = optimize,
                .imports = &.{
                    .{ .name = "dvui", .module = dvui_mod.? },
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
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
            .root_source_file = b.path("editor/Cli.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
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
    const engine_tests = b.addTest(.{ .root_module = engine_mod });
    const editor_tests = b.addTest(.{ .root_module = editor_mod });

    const studio_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("studio/EditorState.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
            },
        }),
    });
    studio_tests.root_module.addOptions("turian_build_options", turian_opts);

    const test_step = b.step("test", "Run engine + editor + studio tests");
    test_step.dependOn(&b.addRunArtifact(engine_tests).step);
    test_step.dependOn(&b.addRunArtifact(editor_tests).step);
    test_step.dependOn(&b.addRunArtifact(studio_tests).step);

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
                .imports = &.{
                    .{ .name = "dvui", .module = dvui_mod.? },
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
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
            .root_source_file = b.path("editor/Cli.zig"),
            .target = target,
            .optimize = .ReleaseFast,
            .imports = &.{
                .{ .name = "engine", .module = engine_mod },
                .{ .name = "editor", .module = editor_mod },
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
                .root_source_file = b.path("editor/Cli.zig"),
                .target = target,
                .optimize = optimize,
                .imports = &.{
                    .{ .name = "engine", .module = engine_mod },
                    .{ .name = "editor", .module = editor_mod },
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
                    .imports = &.{
                        .{ .name = "dvui", .module = dvui_mod.? },
                        .{ .name = "engine", .module = engine_mod },
                        .{ .name = "editor", .module = editor_mod },
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
        }) |d| {
            sdk_step.dependOn(&b.addInstallDirectory(.{
                .source_dir = b.path(d.src),
                .install_dir = .prefix,
                .install_subdir = d.dst,
                .exclude_extensions = &.{"md"},
            }).step);
        }

        // ── Dependency sources (src/ only — deps are self-contained Zig libs) ─
        // Each dep is installed to sdk/deps/<name>/src/ so SdkLayout.zig finds
        // src/root.zig at a fixed relative path; build.zig/docs/examples dropped.
        const dep_installs = [_]struct { dep: *std.Build.Dependency, name: []const u8 }{
            .{ .dep = math3d_dep, .name = "math3d" },
            .{ .dep = guid_dep, .name = "guid" },
            .{ .dep = serde_dep, .name = "serde" },
            .{ .dep = oap_dep, .name = "open_asset_package" },
        };
        for (dep_installs) |di| {
            sdk_step.dependOn(&b.addInstallDirectory(.{
                .source_dir = di.dep.path("src"),
                .install_dir = .prefix,
                .install_subdir = b.fmt("sdk/deps/{s}/src", .{di.name}),
                .exclude_extensions = &.{"md"},
            }).step);
        }

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
