const std = @import("std");
const Shared = @import("CodegenShared.zig");

pub const BuildConfig = Shared.BuildConfig;
pub const RuntimeConfig = Shared.RuntimeConfig;
pub const ModuleSpec = Shared.ModuleSpec;
pub const PluginSpec = Shared.PluginSpec;

pub fn normPath(a: std.mem.Allocator, path: []const u8) ![]const u8 {
    return Shared.normPath(a, path);
}

pub fn appendKtx2Module(a: std.mem.Allocator, out: *std.ArrayList(u8), config: BuildConfig) !void {
    return Shared.appendKtx2Module(a, out, config);
}

pub fn generateReflectionBuildZig(
    a: std.mem.Allocator,
    config: BuildConfig,
    reflection_zig: []const u8,
    source_abs: []const u8,
    wrapper_abs: []const u8,
    lib_name: []const u8,
) ![]u8 {
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(a);

    try out.appendSlice(a, "const std = @import(\"std\");\n\n");
    try out.appendSlice(a, "pub fn build(b: *std.Build) void {\n");
    try out.appendSlice(a, "    const target  = b.standardTargetOptions(.{});\n");
    try out.appendSlice(a, "    const optimize = b.standardOptimizeOption(.{});\n\n");

    // math — engine/root.zig re-exports it, so it must exist.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const math_mod = b.addModule(\"math\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{try normPath(a, config.math_root)},
    ));

    // open_asset_package — used by engine/assets/*.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const oap_mod = b.addModule(\"open_asset_package\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n\n",
        .{try normPath(a, config.oap_root)},
    ));

    // engine (with cgltf + stb_image C sources, link_libc).
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
            "    engine_mod.addImport(\"open_asset_package\", oap_mod);\n\n",
        .{
            try normPath(a, config.engine_root),
            try normPath(a, config.vendor_include),
            try normPath(a, config.cgltf_wrap_c),
            try normPath(a, config.fbx_wrap_c),
            try normPath(a, config.vendor_include),
        },
    ));

    // ktx2 — engine/assets/Texture.zig and ImageLoader.zig import it.
    try appendKtx2Module(a, &out, config);

    // serde — engine/assets/Material.zig imports it.
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
            "    engine_mod.addImport(\"serde\", serde_mod);\n\n",
        .{ try normPath(a, config.serde_compat_root), try normPath(a, config.serde_root) },
    ));

    // reflection — the comptime field-inspector, depends on engine.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const reflection_mod = b.addModule(\"reflection\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    reflection_mod.addImport(\"engine\", engine_mod);\n\n",
        .{try normPath(a, reflection_zig)},
    ));

    // Source package modules: user scripts may @import them, so they
    // must be declared here or the user module will fail to compile.
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
    }
    if (config.extra_modules.len > 0) try out.append(a, '\n');

    // user_module — the script file being reflected.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const user_mod = b.addModule(\"user_module\", .{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "    }});\n" ++
            "    user_mod.addImport(\"engine\", engine_mod);\n",
        .{try normPath(a, source_abs)},
    ));
    for (config.extra_modules, 0..) |m, mi| {
        try out.appendSlice(a, try std.fmt.allocPrint(
            a,
            "    user_mod.addImport(\"{s}\", pkgmod_{d});\n",
            .{ m.name, mi },
        ));
    }
    try out.append(a, '\n');

    // root = generated wrapper (imports engine + reflection + user_module).
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const root_mod = b.createModule(.{{\n" ++
            "        .root_source_file = .{{ .cwd_relative = \"{s}\" }},\n" ++
            "        .target = target,\n" ++
            "        .optimize = optimize,\n" ++
            "        .imports = &.{{\n" ++
            "            .{{ .name = \"engine\",      .module = engine_mod }},\n" ++
            "            .{{ .name = \"reflection\",  .module = reflection_mod }},\n" ++
            "            .{{ .name = \"user_module\", .module = user_mod }},\n" ++
            "        }},\n" ++
            "    }});\n" ++
            "    root_mod.link_libc = true;\n\n",
        .{try normPath(a, wrapper_abs)},
    ));

    // Dynamic library output.
    try out.appendSlice(a, try std.fmt.allocPrint(
        a,
        "    const lib = b.addLibrary(.{{\n" ++
            "        .name = \"{s}\",\n" ++
            "        .root_module = root_mod,\n" ++
            "        .linkage = .dynamic,\n" ++
            "    }});\n" ++
            "    b.installArtifact(lib);\n" ++
            "}}\n",
        .{lib_name},
    ));

    return try out.toOwnedSlice(a);
}
