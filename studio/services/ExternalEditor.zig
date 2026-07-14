//! Opens a source file at a given line in an external code editor, for the
//! Output panel's clickable `file:line` references. Studio has no
//! built-in source editor, so this shells out.
//!
//! Tries `$VISUAL`/`$EDITOR` first, recognizing the `--goto file:line` /
//! `file:line` argv conventions of common editors (code, code-insiders, zed,
//! subl, vim). Falls back to the OS-default file handler (no line jump) when
//! unset or unrecognized.
const std = @import("std");
const gui = @import("gui");
const EditorState = @import("EditorState.zig");

const builtin = @import("builtin");

/// Editors that accept a single `path:line` (or `path:line:col`) argv
/// argument for jumping straight to a location.
const goto_colon_editors = [_][]const u8{ "subl", "sublime_text", "zed", "nvim-remote" };
/// Editors that need an explicit `--goto path:line` flag.
const goto_flag_editors = [_][]const u8{ "code", "code-insiders", "codium" };

fn basename(path: []const u8) []const u8 {
    if (std.mem.lastIndexOfScalar(u8, path, '/')) |i| return path[i + 1 ..];
    return path;
}

fn matchesAny(name: []const u8, list: []const []const u8) bool {
    for (list) |candidate| {
        if (std.mem.eql(u8, name, candidate)) return true;
    }
    return false;
}

/// Opens `path` at `line` in the user's configured editor, or the OS-default
/// handler (without a line jump) if none is configured/recognized.
pub fn openAtLocation(path: []const u8, line: u32) void {
    const editor_cmd = EditorState.environ_map.get("VISUAL") orelse EditorState.environ_map.get("EDITOR");
    if (editor_cmd) |cmd| {
        const name = basename(cmd);
        var loc_buf: [1200]u8 = undefined;

        if (matchesAny(name, &goto_flag_editors)) {
            const loc = std.fmt.bufPrint(&loc_buf, "{s}:{d}", .{ path, line }) catch return;
            const argv = [_][]const u8{ cmd, "--goto", loc };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
            return;
        }
        if (matchesAny(name, &goto_colon_editors)) {
            const loc = std.fmt.bufPrint(&loc_buf, "{s}:{d}", .{ path, line }) catch return;
            const argv = [_][]const u8{ cmd, loc };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
            return;
        }
        if (std.mem.eql(u8, name, "vim") or std.mem.eql(u8, name, "nvim")) {
            const loc = std.fmt.bufPrint(&loc_buf, "+{d}", .{line}) catch return;
            const argv = [_][]const u8{ cmd, loc, path };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
            return;
        }
    }
    openExternalNoLine(path);
}

/// OS-default handler for `path` (no line jump support).
fn openExternalNoLine(path: []const u8) void {
    if (comptime builtin.os.tag == .windows) {
        const argv = [_][]const u8{ "cmd.exe", "/c", "start", "", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    } else if (comptime builtin.os.tag == .macos) {
        const argv = [_][]const u8{ "open", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    } else {
        const argv = [_][]const u8{ "xdg-open", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    }
}
