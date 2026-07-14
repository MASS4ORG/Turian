//! Parses `path:line[:col]` source references out of log/stack-trace text so
//! the Studio Output panel can render them as clickable links.
const std = @import("std");

pub const Location = struct {
    path: []const u8,
    line: u32,
};

const source_exts = [_][]const u8{ ".zig", ".c", ".h", ".cpp", ".hpp" };

/// Finds the first `path:line` reference in `text`, where `path` ends in a
/// recognized source extension. Returns null if none is found.
pub fn find(text: []const u8) ?Location {
    var i: usize = 0;
    while (i < text.len) : (i += 1) {
        if (text[i] != ':') continue;
        const path_start = pathStart(text, i);
        if (path_start == i) continue;
        const path = text[path_start..i];
        if (!looksLikeSourcePath(path)) continue;

        const line_start = i + 1;
        var j = line_start;
        while (j < text.len and std.ascii.isDigit(text[j])) : (j += 1) {}
        if (j == line_start) continue;
        const line = std.fmt.parseInt(u32, text[line_start..j], 10) catch continue;

        return .{ .path = path, .line = line };
    }
    return null;
}

/// Widens leftward from `colon_idx` over path-like characters.
fn pathStart(text: []const u8, colon_idx: usize) usize {
    var s = colon_idx;
    while (s > 0) {
        const c = text[s - 1];
        if (std.ascii.isAlphanumeric(c) or c == '/' or c == '.' or c == '_' or c == '-') {
            s -= 1;
        } else break;
    }
    return s;
}

fn looksLikeSourcePath(path: []const u8) bool {
    for (source_exts) |ext| {
        if (std.mem.endsWith(u8, path, ext)) return true;
    }
    return false;
}

test "finds a plain file:line reference" {
    const loc = find("engine/DiagLog.zig:42: something failed").?;
    try std.testing.expectEqualStrings("engine/DiagLog.zig", loc.path);
    try std.testing.expectEqual(@as(u32, 42), loc.line);
}

test "finds file:line:col reference, ignoring the column" {
    const loc = find("panic at studio/main-window/LogPanel.zig:118:9: index out of bounds").?;
    try std.testing.expectEqualStrings("studio/main-window/LogPanel.zig", loc.path);
    try std.testing.expectEqual(@as(u32, 118), loc.line);
}

test "returns null when there is no source reference" {
    try std.testing.expect(find("plain message, no location here") == null);
    try std.testing.expect(find("ratio: 0.75 assets/foo.png not found") == null);
}
