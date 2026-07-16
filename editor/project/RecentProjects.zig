const std = @import("std");
const Settings = @import("Settings.zig").Settings;

pub const MAX = 10;
pub const KEY = "editor.recent_projects";

/// Add or move `path` to the front of the recent list, trimming to MAX entries.
///
/// `path` is canonicalised to its absolute real path before storing/comparing,
/// so opening the same directory via different spellings (e.g. `../sample-01`
/// and `/dev/sample-01`) does not create duplicate entries.
pub fn push(settings: *Settings, io: std.Io, allocator: std.mem.Allocator, path: []const u8) void {
    var abs_buf: [1024]u8 = undefined;
    const canon = canonical(io, path, &abs_buf);

    var buf: [MAX][]const u8 = undefined;
    var count: usize = 0;
    readPaths(settings, allocator, &buf, &count);
    defer freePaths(allocator, buf[0..count]);

    var out: [MAX][]const u8 = undefined;
    var n: usize = 0;
    out[n] = canon;
    n += 1;
    var entry_buf: [1024]u8 = undefined;
    for (buf[0..count]) |p| {
        if (n >= MAX) break;
        if (std.mem.eql(u8, canonical(io, p, &entry_buf), canon)) continue;
        out[n] = p;
        n += 1;
    }
    writePaths(settings, allocator, out[0..n]);
}

/// Remove the entry whose canonical path matches `path`.
pub fn remove(settings: *Settings, io: std.Io, allocator: std.mem.Allocator, path: []const u8) void {
    var abs_buf: [1024]u8 = undefined;
    const canon = canonical(io, path, &abs_buf);

    var buf: [MAX][]const u8 = undefined;
    var count: usize = 0;
    readPaths(settings, allocator, &buf, &count);
    defer freePaths(allocator, buf[0..count]);

    var out: [MAX][]const u8 = undefined;
    var n: usize = 0;
    var entry_buf: [1024]u8 = undefined;
    for (buf[0..count]) |p| {
        if (std.mem.eql(u8, canonical(io, p, &entry_buf), canon)) continue;
        out[n] = p;
        n += 1;
    }
    writePaths(settings, allocator, out[0..n]);
}

/// Resolve `path` to its canonical absolute path. Falls back to `path`
/// unchanged if it cannot be opened (e.g. the directory no longer exists).
pub fn canonical(io: std.Io, path: []const u8, buf: []u8) []const u8 {
    var dir = std.Io.Dir.cwd().openDir(io, path, .{}) catch return path;
    defer dir.close(io);
    const n = dir.realPath(io, buf) catch return path;
    return buf[0..n];
}

/// Returns an allocated slice of paths, most recent first.
/// Each path and the outer slice are allocated with `allocator`.
/// Call `freeList` when done (no-op for arena allocators).
pub fn list(settings: *Settings, allocator: std.mem.Allocator) [][]const u8 {
    var buf: [MAX][]const u8 = undefined;
    var count: usize = 0;
    readPaths(settings, allocator, &buf, &count);
    const result = allocator.alloc([]const u8, count) catch return &.{};
    @memcpy(result, buf[0..count]);
    return result;
}

pub fn freeList(allocator: std.mem.Allocator, paths: [][]const u8) void {
    for (paths) |p| allocator.free(p);
    allocator.free(paths);
}

// ── Internals ─────────────────────────────────────────────────────────────────

fn readPaths(
    settings: *Settings,
    allocator: std.mem.Allocator,
    out: *[MAX][]const u8,
    count: *usize,
) void {
    count.* = 0;
    const raw = settings.get(KEY) orelse return;
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, raw, .{}) catch return;
    defer parsed.deinit();
    if (parsed.value != .array) return;
    for (parsed.value.array.items) |item| {
        if (count.* >= MAX) break;
        if (item != .string) continue;
        out[count.*] = allocator.dupe(u8, item.string) catch continue;
        count.* += 1;
    }
}

fn freePaths(allocator: std.mem.Allocator, paths: []const []const u8) void {
    for (paths) |p| allocator.free(p);
}

fn writePaths(settings: *Settings, allocator: std.mem.Allocator, paths: []const []const u8) void {
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(allocator);

    out.appendSlice(allocator, "[") catch return;
    for (paths, 0..) |path, i| {
        if (i > 0) out.appendSlice(allocator, ",") catch return;
        out.append(allocator, '"') catch return;
        for (path) |c| switch (c) {
            '"' => out.appendSlice(allocator, "\\\"") catch return,
            '\\' => out.appendSlice(allocator, "\\\\") catch return,
            '\n' => out.appendSlice(allocator, "\\n") catch return,
            '\r' => out.appendSlice(allocator, "\\r") catch return,
            else => out.append(allocator, c) catch return,
        };
        out.append(allocator, '"') catch return;
    }
    out.appendSlice(allocator, "]") catch return;

    settings.set(KEY, out.items) catch {};
}
