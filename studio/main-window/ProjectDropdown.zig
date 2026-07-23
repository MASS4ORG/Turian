//! Project selector dropdown in the main menu bar: current project,
//! recent projects list, and "Open Project...". Recent projects' name/icon
//! are read from disk once and cached for the session.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const engine = @import("engine");
const EditorState = @import("../services/EditorState.zig");
const ProjectOps = @import("../services/ProjectOps.zig");
const PreviewSystem = @import("../asset-browser/preview/PreviewSystem.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

pub fn draw(m: *gui.MenuWidget) void {
    const cur_name = currentProjectName();

    var mi = gui.menuItem(@src(), .{ .submenu = true }, .{ .font = .theme(.heading) });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_y = 0.5 });
        defer row.deinit();
        if (currentProjectIconSource()) |src| drawIcon(src);
        gui.labelNoFmt(@src(), cur_name, .{}, mi.style().strip().override(.{ .label = .{ .for_id = mi.data().id } }));
    }
    // Full absolute project path on hover — helps confirm which project is
    // active when juggling several with similar names.
    if (EditorState.project_path) |p| {
        var abs_buf: [1024]u8 = undefined;
        const abs = editor.recent_projects.canonical(gui.io, p, &abs_buf);
        gui.tooltip(@src(), .{ .active_rect = mi.data().rectScale().r }, "{s}", .{abs}, .{});
    }
    const r = mi.activeRect();
    mi.deinit();

    if (r) |rr| {
        var fw = gui.floatingMenu(@src(), .{ .from = rr }, .{});
        defer fw.deinit();

        if (!EditorState.settingsReady()) {
            gui.label(@src(), "{s}", .{tr("Settings not ready")}, .{ .expand = .horizontal, .padding = .all(8) });
        } else {
            const arena = gui.currentWindow().arena();
            const recent = editor.recent_projects.list(&EditorState.settings, arena);

            if (recent.len == 0) {
                gui.label(@src(), "{s}", .{tr("No recent projects")}, .{ .expand = .horizontal, .padding = .all(8) });
            } else {
                for (recent, 0..) |path, i| drawRecentRow(m, path, i);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), tr("Open Project..."), .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                ProjectOps.openProjectDialog();
            }
        }
    }
}

fn drawIcon(src: gui.ImageSource) void {
    _ = gui.image(@src(), .{ .source = src, .shrink = .ratio }, .{
        .min_size_content = .{ .w = 16, .h = 16 },
        .gravity_y = 0.5,
        .margin = .{ .w = 4 },
    });
}

fn currentProjectName() []const u8 {
    // `|*p|`, not `|p|`: `nameSlice()` returns a slice into `p.name_buf`, so
    // capturing by value would return a slice into a stack copy that's
    // already gone by the time the caller reads it.
    if (EditorState.current_project) |*p| {
        const n = p.nameSlice();
        if (n.len > 0) return n;
    }
    if (EditorState.project_path) |p| return std.fs.path.basename(p);
    return tr("No Project");
}

fn currentProjectIconSource() ?gui.ImageSource {
    const p = if (EditorState.current_project) |*cp| cp else return null;
    const icon_guid = p.iconSlice();
    if (icon_guid.len == 0) return null;
    const path = EditorState.resolveAssetGuid(icon_guid) orelse return null;
    return PreviewSystem.imageSourceFor(path);
}

fn projectDirExists(path: []const u8) bool {
    var d = std.Io.Dir.cwd().openDir(gui.io, path, .{}) catch return false;
    d.close(gui.io);
    return true;
}

fn drawRecentRow(m: *gui.MenuWidget, path: []const u8, i: usize) void {
    const is_current = if (EditorState.project_path) |cur| std.mem.eql(u8, cur, path) else false;
    const exists = projectDirExists(path);
    const e = recentInfo(gui.io, path);
    const name = if (e.name_len > 0) e.name() else std.fs.path.basename(path);

    var lbuf: [300]u8 = undefined;
    const label = if (!exists)
        std.fmt.bufPrint(&lbuf, "[!] {s}", .{name}) catch name
    else if (is_current)
        std.fmt.bufPrint(&lbuf, "* {s}", .{name}) catch name
    else
        name;

    var mi = gui.menuItem(@src(), .{}, .{ .expand = .horizontal, .id_extra = i });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = i });
        defer row.deinit();
        if (recentIconSource(e)) |src| drawIcon(src);
        gui.labelNoFmt(@src(), label, .{}, mi.style().strip().override(.{
            .expand = .horizontal,
            .label = .{ .for_id = mi.data().id },
        }));
    }
    // Recent-list paths are already canonicalised (absolute) by
    // `recent_projects.push`, so the tooltip needs no extra resolution.
    gui.tooltip(@src(), .{ .active_rect = mi.data().rectScale().r }, "{s}", .{path}, .{ .id_extra = i });
    const activated = mi.activeRect() != null;
    mi.deinit();

    if (activated) {
        if (exists and !is_current) {
            m.close();
            ProjectOps.openProject(path);
        } else if (!exists) {
            const arena = gui.currentWindow().arena();
            editor.recent_projects.remove(&EditorState.settings, gui.io, arena, path);
            EditorState.settings.save(gui.io);
        }
    }
}

// ── Cold cache: name + icon for recent (unopened) projects ─────────────────

const RecentCacheEntry = struct {
    path_buf: [768]u8 = undefined,
    path_len: usize = 0,
    name_buf: [128]u8 = undefined,
    name_len: usize = 0,
    icon_pixels: ?[]u8 = null,
    icon_w: u32 = 0,
    icon_h: u32 = 0,

    fn path(self: *const RecentCacheEntry) []const u8 {
        return self.path_buf[0..self.path_len];
    }
    fn name(self: *const RecentCacheEntry) []const u8 {
        return self.name_buf[0..self.name_len];
    }
};

var recent_cache: [editor.recent_projects.MAX]RecentCacheEntry = undefined;
var recent_cache_count: usize = 0;

/// Resolve (and cache) a recent project's display name + icon image by
/// reading its ProjectSettings directly from disk — these projects aren't
/// open, so there is no live asset database to consult. Resolved once per
/// session per path; a project's name/icon edited while off the recent list
/// won't refresh here until the app restarts (a convenience list, not a
/// live view).
fn recentInfo(io: std.Io, project_path: []const u8) *const RecentCacheEntry {
    for (recent_cache[0..recent_cache_count]) |*e| {
        if (std.mem.eql(u8, e.path(), project_path)) return e;
    }

    // Cache full: evict slot 0, which (having been filled to reach
    // capacity) is guaranteed to already hold an initialized entry — unlike
    // a fresh slot, whose `icon_pixels` is undefined memory and unsafe to
    // read before first initializing it below.
    const reused_full = recent_cache_count >= recent_cache.len;
    const idx = if (!reused_full) blk: {
        recent_cache_count += 1;
        break :blk recent_cache_count - 1;
    } else 0;

    const e = &recent_cache[idx];
    if (reused_full) {
        if (e.icon_pixels) |px| std.heap.page_allocator.free(px);
    }
    e.* = .{};
    const n = @min(project_path.len, e.path_buf.len);
    @memcpy(e.path_buf[0..n], project_path[0..n]);
    e.path_len = n;

    resolveRecentInfo(io, project_path, e);
    return e;
}

fn resolveRecentInfo(io: std.Io, project_path: []const u8, e: *RecentCacheEntry) void {
    var assets_buf: [1024]u8 = undefined;
    const assets_dir = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{project_path}) catch return;

    var db = editor.AssetDatabase.init(std.heap.page_allocator);
    defer db.deinit();
    db.scan(io, assets_dir);

    var it = db.enumerate(.project_settings);
    const info = it.next() orelse return;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const bytes = std.Io.Dir.cwd().readFileAlloc(io, info.path, arena, .unlimited) catch return;
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

fn recentIconSource(e: *const RecentCacheEntry) ?gui.ImageSource {
    const pixels = e.icon_pixels orelse return null;
    return .{ .pixels = .{ .rgba = pixels, .width = e.icon_w, .height = e.icon_h, .invalidation = .ptr } };
}
