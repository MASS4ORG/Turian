/// SDK layout resolution — three-layer priority for GameBuild.BuildConfig:
///   1. TURIAN_* env var overrides  (highest)
///   2. SDK-relative paths          (when running from an installed SDK bundle)
///   3. baked-in build_options      (in-tree dev builds)
///
/// SDK detection: the executable lives at <sdk>/bin/turian-cli[.exe].
/// If <sdk>/turian-sdk.json exists, we're in SDK mode and all paths are
/// resolved relative to <sdk>/.
const std = @import("std");
const GameBuild = @import("GameBuild.zig");
const package_store = @import("PackageStore.zig");

/// Detect the SDK root from the current executable's directory.
/// Returns an owned slice (caller must free) or null if not in an SDK.
fn detectSdkRoot(io: std.Io, gpa: std.mem.Allocator) ?[]const u8 {
    const exe_dir = std.process.executableDirPathAlloc(io, gpa) catch return null;
    defer gpa.free(exe_dir);

    // Binaries live at the SDK root, alongside turian-sdk.json.
    const marker = std.fmt.allocPrint(gpa, "{s}/turian-sdk.json", .{exe_dir}) catch return null;
    defer gpa.free(marker);

    // Confirm the marker file exists.
    var f = std.Io.Dir.cwd().openFile(io, marker, .{}) catch return null;
    f.close(io);

    return gpa.dupe(u8, exe_dir) catch null;
}

/// Build a GameBuild.BuildConfig from an SDK root path.
/// All returned slices are allocated with `gpa`.
fn configFromSdk(io: std.Io, gpa: std.mem.Allocator, sdk_root: []const u8) GameBuild.BuildConfig {
    const p = struct {
        fn join(a: std.mem.Allocator, root: []const u8, suffix: []const u8) []const u8 {
            return std.fmt.allocPrint(a, "{s}/{s}", .{ root, suffix }) catch suffix;
        }
        fn exists(i: std.Io, path: []const u8) bool {
            var f = std.Io.Dir.cwd().openFile(i, path, .{}) catch return false;
            f.close(i);
            return true;
        }
    };

    // Pick the SDL3 lib path that actually exists in this SDK — empty means
    // "don't link SDL3" (CLI-only SDKs ship without the lib/ directory).
    const sdl3_candidates = [_][]const u8{ "lib/libSDL3.a", "lib/SDL3.lib", "lib/libSDL3.dylib" };
    const sdl3_lib: []const u8 = blk: {
        for (sdl3_candidates) |suffix| {
            const candidate = p.join(gpa, sdk_root, suffix);
            if (p.exists(io, candidate)) break :blk candidate;
            gpa.free(candidate);
        }
        break :blk "";
    };

    return .{
        .engine_root = p.join(gpa, sdk_root, "engine/root.zig"),
        .editor_root = p.join(gpa, sdk_root, "editor/root.zig"),
        .cgltf_wrap_c = p.join(gpa, sdk_root, "engine/vendor/cgltf_wrap.c"),
        .vendor_include = p.join(gpa, sdk_root, "engine/vendor"),
        .build_root = gpa.dupe(u8, sdk_root) catch sdk_root,
        .sdl3_lib = sdl3_lib,
        .math_root = p.join(gpa, sdk_root, "deps/math3d/src/root.zig"),
        .guid_root = p.join(gpa, sdk_root, "deps/guid/src/root.zig"),
        .oap_root = p.join(gpa, sdk_root, "deps/open_asset_package/src/root.zig"),
        .serde_root = p.join(gpa, sdk_root, "deps/serde/src/root.zig"),
        .serde_compat_root = p.join(gpa, sdk_root, "deps/serde/src/compat_0_16.zig"),
        .ktx2_root = p.join(gpa, sdk_root, "deps/ktx2/src/root.zig"),
        .gpu_root = p.join(gpa, sdk_root, "deps/gpu/src/root.zig"),
        .gpu_sdl3_c = p.join(gpa, sdk_root, "deps/gpu/src/sdl3-c.h"),
        .render_root = p.join(gpa, sdk_root, "render/root.zig"),
        .sdl3_include = p.join(gpa, sdk_root, "sdl3-include"),
    };
}

/// Apply TURIAN_* environment variable overrides to a config.
/// Fields in `cfg` are replaced in-place (old slices are NOT freed — use an arena).
fn applyEnvOverrides(cfg: *GameBuild.BuildConfig, environ: *const std.process.Environ.Map) void {
    if (environ.get("TURIAN_ENGINE_ROOT")) |v| cfg.engine_root = v;
    if (environ.get("TURIAN_EDITOR_ROOT")) |v| cfg.editor_root = v;
    if (environ.get("TURIAN_CGLTF_WRAP_C")) |v| cfg.cgltf_wrap_c = v;
    if (environ.get("TURIAN_VENDOR_INCLUDE")) |v| cfg.vendor_include = v;
    if (environ.get("TURIAN_BUILD_ROOT")) |v| cfg.build_root = v;
    if (environ.get("TURIAN_SDL3_LIB")) |v| cfg.sdl3_lib = v;
    if (environ.get("TURIAN_MATH3D_ROOT")) |v| cfg.math_root = v;
    if (environ.get("TURIAN_GUID_ROOT")) |v| cfg.guid_root = v;
    if (environ.get("TURIAN_OAP_ROOT")) |v| cfg.oap_root = v;
    if (environ.get("TURIAN_SERDE_ROOT")) |v| cfg.serde_root = v;
    if (environ.get("TURIAN_SERDE_COMPAT_ROOT")) |v| cfg.serde_compat_root = v;
    if (environ.get("TURIAN_KTX2_ROOT")) |v| cfg.ktx2_root = v;
    if (environ.get("TURIAN_GPU_ROOT")) |v| cfg.gpu_root = v;
    if (environ.get("TURIAN_GPU_SDL3_C")) |v| cfg.gpu_sdl3_c = v;
    if (environ.get("TURIAN_RENDER_ROOT")) |v| cfg.render_root = v;
    if (environ.get("TURIAN_SDL3_INCLUDE")) |v| cfg.sdl3_include = v;
}

/// Resolve a UserReflection.ReflectionConfig using the same three-layer
/// priority as `resolveBuildConfig`.  `baked` is the compile-time default
/// BuildConfig (from build_options); the returned config's string slices are
/// owned by `gpa` (SDK paths) or borrowed from `baked` (dev paths).
///
/// `reflection_zig` is derived from the resolved engine_root so it stays
/// consistent with whichever SDK layout wins.
pub fn resolveReflectionConfig(
    io: std.Io,
    gpa: std.mem.Allocator,
    environ: *const std.process.Environ.Map,
    baked: GameBuild.BuildConfig,
) @import("UserReflection.zig").ReflectionConfig {
    const build_cfg = resolveBuildConfig(io, gpa, environ, baked);
    // Derive reflection_zig from the resolved engine_root:
    //   engine_root = {engine_dir}/root.zig  →  reflection_zig = {engine_dir}/Reflection.zig
    // This works in both SDK mode and dev mode without an extra baked path.
    const engine_dir = std.fs.path.dirname(build_cfg.engine_root) orelse build_cfg.build_root;
    const reflection_zig = std.fmt.allocPrint(gpa, "{s}/Reflection.zig", .{engine_dir}) catch build_cfg.engine_root;
    return .{ .reflection_zig = reflection_zig, .build_config = build_cfg };
}

/// Resolve a GameBuild.BuildConfig with three-layer priority:
///   1. TURIAN_* env vars  (highest)
///   2. SDK-relative paths (when <sdk>/turian-sdk.json is present)
///   3. `baked` defaults   (in-tree dev build)
///
/// The returned config's string slices are either owned by `gpa` (SDK
/// paths), borrowed from `baked` (dev paths), or borrowed from the environ
/// map (env overrides).  The caller owns anything allocated via `gpa`.
pub fn resolveBuildConfig(
    io: std.Io,
    gpa: std.mem.Allocator,
    environ: *const std.process.Environ.Map,
    baked: GameBuild.BuildConfig,
) GameBuild.BuildConfig {
    var cfg: GameBuild.BuildConfig = if (detectSdkRoot(io, gpa)) |sdk_root| blk: {
        defer gpa.free(sdk_root);
        break :blk configFromSdk(io, gpa, sdk_root);
    } else baked;

    // `engine_version` is a build-time constant, not an SDK-layout path, so it is
    // never set by `configFromSdk`; carry it over from the baked config.
    cfg.engine_version = baked.engine_version;
    // Resolve the central package store root from the environment (issue #20).
    cfg.package_store = package_store.resolveRoot(gpa, environ) catch "";
    applyEnvOverrides(&cfg, environ);
    return cfg;
}
