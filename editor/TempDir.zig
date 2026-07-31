/// OS temporary-directory resolution from the process environment.
const std = @import("std");

const is_windows = @import("builtin").os.tag == .windows;

/// The directory for scratch files, borrowed from `environ` when set.
/// Windows has no `/tmp` and `C:\Windows\Temp` is administrator-only, so
/// `TEMP`/`TMP` are the only dependable sources on that platform.
pub fn resolve(environ: *const std.process.Environ.Map) []const u8 {
    const vars: []const []const u8 = if (is_windows)
        &.{ "TEMP", "TMP" }
    else
        &.{"TMPDIR"};
    for (vars) |name| {
        if (environ.get(name)) |v| {
            if (v.len > 0) return v;
        }
    }
    return fallback;
}

/// Used when the environment names no temporary directory.
pub const fallback: []const u8 = if (is_windows) "C:\\Windows\\Temp" else "/tmp";

test "resolve prefers the platform's temp variable" {
    var map: std.process.Environ.Map = .init(std.testing.allocator);
    defer map.deinit();
    try map.put(if (is_windows) "TEMP" else "TMPDIR", "/scratch");
    try std.testing.expectEqualStrings("/scratch", resolve(&map));
}

test "resolve ignores an empty value and falls back" {
    var map: std.process.Environ.Map = .init(std.testing.allocator);
    defer map.deinit();
    try map.put(if (is_windows) "TEMP" else "TMPDIR", "");
    try std.testing.expectEqualStrings(fallback, resolve(&map));
}

test "resolve falls back when unset" {
    var map: std.process.Environ.Map = .init(std.testing.allocator);
    defer map.deinit();
    try std.testing.expectEqualStrings(fallback, resolve(&map));
}
