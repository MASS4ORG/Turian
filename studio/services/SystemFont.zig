//! Registers a user-picked system font file (any `.ttf`/`.otf` reachable via
//! the native file dialog — the practical way to reach OS font directories
//! cross-platform without a bespoke font-enumeration UI) with dvui, so it can
//! override the active theme's fonts (see `ui_render.theme.withFontFamily`).
//!
//! Mirrors `studio/inspector/editor/FontRegistry.zig`'s register-once
//! idiom (dvui's `Window.addFont` only appends, never replaces), but keyed by
//! file path instead of asset GUID since a system font isn't a project asset.
const std = @import("std");
const gui = @import("gui");

var registered_path_buf: [1024]u8 = undefined;
var registered_path_len: usize = 0;

fn isRegistered(path: []const u8) bool {
    return registered_path_len == path.len and std.mem.eql(u8, registered_path_buf[0..registered_path_len], path);
}

/// Ensures `path`'s font is registered with `win` under `path` itself as the
/// dvui family name (a stable, collision-free key — it doesn't need to read
/// as a real family name). Returns the family name to build a `gui.Font`
/// with, or null if the file couldn't be read or dvui rejected it.
///
/// Takes `win`/`io` explicitly rather than `gui.currentWindow()`/`gui.io`:
/// this can run at Studio boot, before the first `Window.begin`.
pub fn ensure(win: *gui.Window, io: std.Io, path: []const u8) ?[]const u8 {
    if (path.len == 0 or path.len > registered_path_buf.len) return null;
    if (isRegistered(path)) return registered_path_buf[0..registered_path_len];

    const bytes = std.Io.Dir.cwd().readFileAlloc(io, path, std.heap.page_allocator, .unlimited) catch return null;
    win.addFont(path, bytes, std.heap.page_allocator) catch {
        std.heap.page_allocator.free(bytes);
        return null;
    };

    @memcpy(registered_path_buf[0..path.len], path);
    registered_path_len = path.len;
    return registered_path_buf[0..registered_path_len];
}
