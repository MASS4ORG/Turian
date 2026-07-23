//! Studio-wide theme discovery: built-in presets plus user `.uitheme` files.
//! Pure data/IO logic only — no GUI dependency (matching `editor/`'s boundary).
const std = @import("std");
const engine = @import("engine");

const UiTheme = engine.UiTheme;

pub const THEMES_SUBDIR = "themes";

/// One entry in the combined built-in + user theme list.
pub const ThemeEntry = struct {
    name: []const u8,
    builtin: bool,
};

/// A theme resolved by name, tagged with whether the caller owns its slices
/// (loaded from disk) or not (a static built-in — must NOT be passed to
/// `UiTheme.deinit`).
pub const Resolved = struct {
    theme: UiTheme,
    owned: bool,

    pub fn deinit(self: *Resolved, allocator: std.mem.Allocator) void {
        if (self.owned) self.theme.deinit(allocator);
    }
};

/// `<dirname(global_settings_path)>/themes` — sibling of `settings.json`
/// inside the global `.turian` directory.
pub fn themesDir(allocator: std.mem.Allocator, global_settings_path: []const u8) ![]u8 {
    const settings_dir = std.fs.path.dirname(global_settings_path) orelse ".";
    return std.fmt.allocPrint(allocator, "{s}{s}{s}", .{ settings_dir, std.fs.path.sep_str, THEMES_SUBDIR });
}

/// Built-ins, then user themes found directly under `themes_dir` (non-recursive),
/// sorted alphabetically within each group. Caller owns the returned slice and
/// each entry's `name` (allocated with `allocator`).
pub fn list(allocator: std.mem.Allocator, io: std.Io, themes_dir: []const u8) ![]ThemeEntry {
    var entries: std.ArrayList(ThemeEntry) = .empty;
    for (engine.ui_theme_presets.all) |t| {
        try entries.append(allocator, .{ .name = try allocator.dupe(u8, t.name), .builtin = true });
    }

    var dir = std.Io.Dir.cwd().openDir(io, themes_dir, .{ .iterate = true }) catch return entries.toOwnedSlice(allocator);
    defer dir.close(io);
    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        if (entry.kind != .file or !std.mem.endsWith(u8, entry.name, ".uitheme")) continue;
        var path_buf: [1024]u8 = undefined;
        const path = std.fmt.bufPrint(&path_buf, "{s}{s}{s}", .{ themes_dir, std.fs.path.sep_str, entry.name }) catch continue;
        var t = UiTheme.load(allocator, io, path) catch continue;
        defer t.deinit(allocator);
        try entries.append(allocator, .{ .name = try allocator.dupe(u8, t.name), .builtin = false });
    }

    return entries.toOwnedSlice(allocator);
}

/// Resolve a theme by its declared `name` — a built-in first, else the first
/// matching `.uitheme` file directly under `themes_dir`. Null if not found.
pub fn resolve(allocator: std.mem.Allocator, io: std.Io, themes_dir: []const u8, name: []const u8) ?Resolved {
    for (engine.ui_theme_presets.all) |t| {
        if (std.mem.eql(u8, t.name, name)) return .{ .theme = t, .owned = false };
    }

    var dir = std.Io.Dir.cwd().openDir(io, themes_dir, .{ .iterate = true }) catch return null;
    defer dir.close(io);
    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        if (entry.kind != .file or !std.mem.endsWith(u8, entry.name, ".uitheme")) continue;
        var path_buf: [1024]u8 = undefined;
        const path = std.fmt.bufPrint(&path_buf, "{s}{s}{s}", .{ themes_dir, std.fs.path.sep_str, entry.name }) catch continue;
        var t = UiTheme.load(allocator, io, path) catch continue;
        if (std.mem.eql(u8, t.name, name)) return .{ .theme = t, .owned = true };
        t.deinit(allocator);
    }
    return null;
}

/// Validate `src_path` as a `.uitheme` file and copy it into `themes_dir`
/// (created if missing), keeping its original filename.
pub fn importFile(allocator: std.mem.Allocator, io: std.Io, themes_dir: []const u8, src_path: []const u8) !void {
    const bytes = try std.Io.Dir.cwd().readFileAlloc(io, src_path, allocator, .unlimited);
    defer allocator.free(bytes);
    var t = try UiTheme.loadFromBytes(allocator, bytes);
    t.deinit(allocator);

    try std.Io.Dir.cwd().createDirPath(io, themes_dir);
    const base = std.fs.path.basename(src_path);
    var dest_buf: [1024]u8 = undefined;
    const dest = try std.fmt.bufPrint(&dest_buf, "{s}{s}{s}", .{ themes_dir, std.fs.path.sep_str, base });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = dest, .data = bytes });
}

/// Write the theme named `name` (built-in or user) to `dest_path`.
pub fn exportTo(allocator: std.mem.Allocator, io: std.Io, themes_dir: []const u8, name: []const u8, dest_path: []const u8) !void {
    var r = resolve(allocator, io, themes_dir, name) orelse return error.ThemeNotFound;
    defer r.deinit(allocator);
    try r.theme.save(io, dest_path);
}

// ── Tests ────────────────────────────────────────────────────────────────────

test "themesDir sits next to the global settings file" {
    const dir = try themesDir(std.testing.allocator, "/home/user/.turian/settings.json");
    defer std.testing.allocator.free(dir);
    try std.testing.expectEqualStrings("/home/user/.turian/themes", dir);
}

test "list includes built-ins even with no themes directory yet" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    const entries = try list(a, io, ".zig-cache/tmp/nonexistent-themes-dir");
    try std.testing.expect(entries.len == engine.ui_theme_presets.all.len);
    for (entries) |e| try std.testing.expect(e.builtin);
}

test "resolve finds a built-in by name" {
    const io = std.testing.io;
    var r = resolve(std.testing.allocator, io, ".zig-cache/tmp/nonexistent-themes-dir", "Dark") orelse return error.NotFound;
    defer r.deinit(std.testing.allocator);
    try std.testing.expect(!r.owned);
    try std.testing.expect(r.theme.dark);
}

test "import, list, resolve, and export round-trip a user theme" {
    const io = std.testing.io;
    var arena_state = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();

    var src_buf: [256]u8 = undefined;
    const src_path = try std.fmt.bufPrint(&src_buf, ".zig-cache/tmp/{s}/src.uitheme", .{tmp.sub_path});
    const custom = UiTheme{ .name = "My Custom Theme", .dark = true };
    try custom.save(io, src_path);

    var dir_buf: [256]u8 = undefined;
    const themes_dir = try std.fmt.bufPrint(&dir_buf, ".zig-cache/tmp/{s}/themes", .{tmp.sub_path});
    try importFile(a, io, themes_dir, src_path);

    const entries = try list(a, io, themes_dir);
    var found = false;
    for (entries) |e| {
        if (!e.builtin and std.mem.eql(u8, e.name, "My Custom Theme")) found = true;
    }
    try std.testing.expect(found);

    var r = resolve(a, io, themes_dir, "My Custom Theme") orelse return error.NotFound;
    defer r.deinit(a);
    try std.testing.expect(r.owned);
    try std.testing.expect(r.theme.dark);

    var export_buf: [256]u8 = undefined;
    const export_path = try std.fmt.bufPrint(&export_buf, ".zig-cache/tmp/{s}/exported.uitheme", .{tmp.sub_path});
    try exportTo(a, io, themes_dir, "My Custom Theme", export_path);

    var exported = try UiTheme.load(a, io, export_path);
    defer exported.deinit(a);
    try std.testing.expectEqualStrings("My Custom Theme", exported.name);
}

test {
    std.testing.refAllDecls(@This());
}
