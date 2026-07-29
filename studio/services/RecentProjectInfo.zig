//! Cold cache: display name + icon for recent (unopened) projects, read from
//! disk once per session. Shared by `ProjectDropdown` (menu bar) and
//! `WelcomePanel` so both list recent projects identically.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const engine = @import("engine");

pub const CacheEntry = struct {
    path_buf: [768]u8 = undefined,
    path_len: usize = 0,
    name_buf: [128]u8 = undefined,
    name_len: usize = 0,
    icon_pixels: ?[]u8 = null,
    icon_w: u32 = 0,
    icon_h: u32 = 0,

    pub fn path(self: *const CacheEntry) []const u8 {
        return self.path_buf[0..self.path_len];
    }
    pub fn name(self: *const CacheEntry) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    /// Resolved display name, falling back to the folder name when the
    /// project has none (or hasn't resolved yet).
    pub fn displayName(self: *const CacheEntry) []const u8 {
        if (self.name_len > 0) return self.name();
        return std.fs.path.basename(self.path());
    }
};

var cache: [editor.recent_projects.MAX]CacheEntry = undefined;
var cache_count: usize = 0;

/// Resolve (and cache) a recent project's display name + icon image by
/// reading its ProjectSettings directly from disk — these projects aren't
/// open, so there is no live asset database to consult. Resolved once per
/// session per path; a project's name/icon edited while off the recent list
/// won't refresh here until the app restarts (a convenience list, not a
/// live view).
pub fn info(io: std.Io, project_path: []const u8) *const CacheEntry {
    for (cache[0..cache_count]) |*e| {
        if (std.mem.eql(u8, e.path(), project_path)) return e;
    }

    // Cache full: evict slot 0, which (having been filled to reach
    // capacity) is guaranteed to already hold an initialized entry — unlike
    // a fresh slot, whose `icon_pixels` is undefined memory and unsafe to
    // read before first initializing it below.
    const reused_full = cache_count >= cache.len;
    const idx = if (!reused_full) blk: {
        cache_count += 1;
        break :blk cache_count - 1;
    } else 0;

    const e = &cache[idx];
    if (reused_full) {
        if (e.icon_pixels) |px| std.heap.page_allocator.free(px);
    }
    e.* = .{};
    const n = @min(project_path.len, e.path_buf.len);
    @memcpy(e.path_buf[0..n], project_path[0..n]);
    e.path_len = n;

    resolve(io, project_path, e);
    return e;
}

fn resolve(io: std.Io, project_path: []const u8, e: *CacheEntry) void {
    var assets_buf: [1024]u8 = undefined;
    const assets_dir = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{project_path}) catch return;

    var db = editor.AssetDatabase.init(std.heap.page_allocator);
    defer db.deinit();
    db.scan(io, assets_dir);

    var it = db.enumerate(.project_settings);
    const proj_info = it.next() orelse return;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const bytes = std.Io.Dir.cwd().readFileAlloc(io, proj_info.path, arena, .unlimited) catch return;
    const ps = engine.ProjectSettings.loadFromBytes(arena, bytes) catch return;

    if (ps.project.name.len > 0) {
        const n = @min(ps.project.name.len, e.name_buf.len);
        @memcpy(e.name_buf[0..n], ps.project.name[0..n]);
        e.name_len = n;
    }

    if (ps.project.icon.len == 0) return;
    const gid = editor.Guid.parse(ps.project.icon) catch return;
    const iinfo = db.findByGuid(gid) orelse return;
    if (iinfo.asset_type != .image) return;

    const file_bytes = std.Io.Dir.cwd().readFileAlloc(io, iinfo.path, std.heap.page_allocator, .unlimited) catch return;
    defer std.heap.page_allocator.free(file_bytes);
    var tex = engine.assets.ImageLoader.loadFromMemory(std.heap.page_allocator, file_bytes) catch return;
    if (tex.isCompressed()) {
        tex.deinit();
        return;
    }
    e.icon_pixels = tex.data;
    e.icon_w = tex.width;
    e.icon_h = tex.height;
}

pub fn iconSource(e: *const CacheEntry) ?gui.ImageSource {
    const pixels = e.icon_pixels orelse return null;
    return .{ .pixels = .{ .rgba = pixels, .width = e.icon_w, .height = e.icon_h, .invalidation = .ptr } };
}
