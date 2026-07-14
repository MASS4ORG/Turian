/// Zig source-file generator types and utilities — build.zig and main.zig.
/// Pure functions; no I/O, no GUI dependency.
const std = @import("std");

/// Window / runtime options baked into the generated game from the project's
/// `ProjectSettings` asset. Defaults mirror `ProjectSettings`.
pub const RuntimeConfig = struct {
    title: []const u8 = "Turian Game",
    width: u32 = 1280,
    height: u32 = 720,
    vsync: bool = true,
    /// GUID of the scene the game boots into (loaded through the SceneManager,
    ///). Empty if no scene could be resolved.
    boot_scene_guid: []const u8 = "",
    /// True when the project's asset database has at least one `.uidoc`
    /// asset (C10 pay-for-use): gates whether `gui`/`ui_render`/dvui are
    /// wired into the generated build at all. A project that never
    /// references Guinevere ships with zero dvui linkage.
    uses_ui: bool = false,
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
    /// Additional asset-root directories contributed by installed packages.
    /// Each entry is an absolute path to an asset directory.
    extra_asset_roots: []const []const u8 = &.{},
    /// Names of third-party dependencies declared in the project's
    /// `project.json` / generated `build.zig.zon`. The generated build
    /// resolves each via `b.dependency()` so Zig's package manager fetches and
    /// hash-pins them. Source/native packages extend this seam to
    /// `addImport`/`linkLibrary` the resolved artifacts.
    extra_deps: []const []const u8 = &.{},
    /// Running engine/SDK version (e.g. "1.8.0"), used to validate installed
    /// packages' `engine_compat` ranges. Empty disables the check.
    engine_version: []const u8 = "",
    /// Central package store root . Asset/hybrid packages recorded in
    /// `project.json` are resolved from here. Empty = store discovery disabled.
    package_store: []const u8 = "",
    /// Native libraries contributed by installed packages, linked into the
    /// game executable.
    extra_native: []const NativeLibSpec = &.{},
    /// Zig source modules exported by installed packages. Each becomes a
    /// `b.addModule` importable from `main.zig` and from user scripts.
    extra_modules: []const ModuleSpec = &.{},
    /// Path to subsystems/ui_render/root.zig (in-game GUI tree-walk, shared
    /// with the Studio viewport overlay). Empty in CLI-only/SDK builds that
    /// don't ship dvui — see `dvui_url`/`dvui_hash`.
    ui_render_root: []const u8 = "",
    /// dvui's pinned `url`/`hash` (C10 pay-for-use): only games that
    /// reference a `.uidoc` asset get a `dvui` entry injected into their
    /// generated `build.zig.zon`, so a project with no UI ships with zero
    /// dvui linkage. Empty disables UI rendering entirely (e.g. SDK builds,
    /// which don't currently vendor dvui).
    dvui_url: []const u8 = "",
    dvui_hash: []const u8 = "",
};

/// A Zig source module exported by a source/hybrid package.
pub const ModuleSpec = struct {
    /// Module import name (e.g. "platformer").
    name: []const u8,
    /// Absolute path to the module's root `.zig` file.
    root_abs: []const u8,
};

/// A plugin runtime-registration entry point: call `module.entry(&services)`
/// at startup so the package can register components/systems/services.
pub const PluginSpec = struct {
    /// Name of the exported module that holds the entry function.
    module: []const u8,
    /// Entry function name, called as `module.entry(&g_services)`.
    entry: []const u8,
};

/// A precompiled native library contributed by a package. `lib_template`
/// is a package-root-relative path that may contain `{target}` (replaced with
/// the build's Zig target triple at build time, e.g. `lib/{target}/libfoo.a`).
pub const NativeLibSpec = struct {
    /// Absolute path to the package root directory.
    pkg_root: []const u8,
    /// System library name (for shared libs) / diagnostic name.
    name: []const u8,
    /// "static" or "shared".
    kind: []const u8,
    /// Package-root-relative path template to the library file.
    lib_template: []const u8,
    /// Package-root-relative include directory (may be empty).
    include: []const u8,
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
