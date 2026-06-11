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
}

/// Resolve a UserReflection.ReflectionConfig with the same priority logic.
/// Returns paths owned by `gpa` (SDK mode) or borrowed from build_options (dev mode).
pub fn resolveReflectionConfig(
    io: std.Io,
    gpa: std.mem.Allocator,
    baked_reflection_zig: []const u8,
    baked_engine_root: []const u8,
) @import("UserReflection.zig").ReflectionConfig {
    const p = struct {
        fn join(a: std.mem.Allocator, root: []const u8, suffix: []const u8) []const u8 {
            return std.fmt.allocPrint(a, "{s}/{s}", .{ root, suffix }) catch suffix;
        }
    };
    if (detectSdkRoot(io, gpa)) |sdk_root| {
        defer gpa.free(sdk_root);
        return .{
            .reflection_zig = p.join(gpa, sdk_root, "engine/Reflection.zig"),
            .engine_root = p.join(gpa, sdk_root, "engine/root.zig"),
        };
    }
    return .{
        .reflection_zig = baked_reflection_zig,
        .engine_root = baked_engine_root,
    };
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

    applyEnvOverrides(&cfg, environ);
    return cfg;
}
