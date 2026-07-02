/// The **central package store**: a machine-wide, version-keyed cache of
/// installed Turian packages, shared across all projects (issue #20, follow-up).
///
/// Layout: `<store-root>/<name>/<version>/` — e.g.
/// `~/.cache/turian/packages/com.acme.kit/1.2.0/`. Multiple versions of the same
/// package coexist. A project records which packages+versions it uses in its
/// `project.json` `packages` map; `PackageManager.discover` resolves those to
/// store paths. This is the local cache a future registry server populates.
///
/// Root resolution order:
///   1. `$TURIAN_PACKAGE_HOME`
///   2. `$XDG_CACHE_HOME/turian/packages`
///   3. `$HOME/.cache/turian/packages`  (POSIX)  /  `$APPDATA/turian/packages` (Windows)
///   4. a temp-dir fallback (so headless/CI without HOME still works)
const std = @import("std");

const SUBDIR = "turian" ++ std.fs.path.sep_str ++ "packages";

/// Resolve the store root directory, allocated with `allocator` (caller owns).
pub fn resolveRoot(allocator: std.mem.Allocator, environ: *const std.process.Environ.Map) ![]u8 {
    if (environ.get("TURIAN_PACKAGE_HOME")) |v| {
        if (v.len > 0) return allocator.dupe(u8, v);
    }
    if (environ.get("XDG_CACHE_HOME")) |v| {
        if (v.len > 0) return std.fmt.allocPrint(allocator, "{s}{s}{s}", .{ v, std.fs.path.sep_str, SUBDIR });
    }
    const home_var = if (@import("builtin").os.tag == .windows) "APPDATA" else "HOME";
    if (environ.get(home_var)) |v| {
        if (v.len > 0) {
            const mid = if (@import("builtin").os.tag == .windows) "" else ".cache" ++ std.fs.path.sep_str;
            return std.fmt.allocPrint(allocator, "{s}{s}{s}{s}", .{ v, std.fs.path.sep_str, mid, SUBDIR });
        }
    }
    const tmp = if (@import("builtin").os.tag == .windows) "C:\\Windows\\Temp" else "/tmp";
    return std.fmt.allocPrint(allocator, "{s}{s}{s}", .{ tmp, std.fs.path.sep_str, SUBDIR });
}

/// Absolute path to a package's versioned directory in the store.
/// `store_root/name/version`. Caller owns the result.
pub fn packagePath(
    allocator: std.mem.Allocator,
    store_root: []const u8,
    name: []const u8,
    version: []const u8,
) ![]u8 {
    return std.fmt.allocPrint(allocator, "{s}/{s}/{s}", .{ store_root, name, version });
}

/// True if `<store_root>/<name>/<version>/turian-package.json` exists.
pub fn isInstalled(
    io: std.Io,
    allocator: std.mem.Allocator,
    store_root: []const u8,
    name: []const u8,
    version: []const u8,
) bool {
    const dir = packagePath(allocator, store_root, name, version) catch return false;
    defer allocator.free(dir);
    var mbuf: [1024]u8 = undefined;
    const manifest = std.fmt.bufPrint(&mbuf, "{s}/turian-package.json", .{dir}) catch return false;
    var f = std.Io.Dir.cwd().openFile(io, manifest, .{}) catch return false;
    f.close(io);
    return true;
}

test "resolveRoot honors TURIAN_PACKAGE_HOME" {
    var map = std.process.Environ.Map.init(std.testing.allocator);
    defer map.deinit();
    try map.put("TURIAN_PACKAGE_HOME", "/custom/store");
    const root = try resolveRoot(std.testing.allocator, &map);
    defer std.testing.allocator.free(root);
    try std.testing.expectEqualStrings("/custom/store", root);
}

test "resolveRoot falls back to XDG then HOME" {
    var map = std.process.Environ.Map.init(std.testing.allocator);
    defer map.deinit();
    try map.put("XDG_CACHE_HOME", "/x/cache");
    const root = try resolveRoot(std.testing.allocator, &map);
    defer std.testing.allocator.free(root);
    try std.testing.expect(std.mem.indexOf(u8, root, "/x/cache") != null);
    try std.testing.expect(std.mem.endsWith(u8, root, "packages"));
}

test "packagePath composes name and version" {
    const p = try packagePath(std.testing.allocator, "/store", "com.acme.kit", "1.2.0");
    defer std.testing.allocator.free(p);
    try std.testing.expectEqualStrings("/store/com.acme.kit/1.2.0", p);
}
