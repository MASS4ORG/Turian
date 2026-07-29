//! Project selector dropdown in the main menu bar: current project,
//! recent projects list, and "Open Project...". Recent projects' name/icon
//! are read from disk once and cached for the session.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("editor").EditorState;
const ProjectOps = @import("../services/ProjectOps.zig");
const PreviewSystem = @import("../asset-browser/preview/PreviewSystem.zig");
const RecentProjectInfo = @import("../services/RecentProjectInfo.zig");
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

fn removeFromRecent(path: []const u8) void {
    const arena = gui.currentWindow().arena();
    editor.recent_projects.remove(&EditorState.settings, gui.io, arena, path);
    EditorState.settings.save(gui.io);
}

fn drawRecentRow(m: *gui.MenuWidget, path: []const u8, i: usize) void {
    const is_current = if (EditorState.project_path) |cur| std.mem.eql(u8, cur, path) else false;
    const exists = projectDirExists(path);
    const e = RecentProjectInfo.info(gui.io, path);
    const name = e.displayName();

    var lbuf: [300]u8 = undefined;
    const label = if (!exists)
        std.fmt.bufPrint(&lbuf, "[!] {s}", .{name}) catch name
    else if (is_current)
        std.fmt.bufPrint(&lbuf, "* {s}", .{name}) catch name
    else
        name;

    var outer = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = i });
    defer outer.deinit();

    var mi = gui.menuItem(@src(), .{}, .{ .id_extra = i, .expand = .horizontal });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_y = 0.5, .id_extra = i });
        defer row.deinit();
        if (RecentProjectInfo.iconSource(e)) |src| drawIcon(src);
        gui.labelNoFmt(@src(), label, .{}, mi.style().strip().override(.{
            .label = .{ .for_id = mi.data().id },
        }));
    }
    // Recent-list paths are already canonicalised (absolute) by
    // `recent_projects.push`, so the tooltip needs no extra resolution.
    gui.tooltip(@src(), .{ .active_rect = mi.data().rectScale().r }, "{s}", .{path}, .{ .id_extra = i });
    const activated = mi.activeRect() != null;
    mi.deinit();

    // Trailing buttons are separate widgets to keep their clicks independent.
    if (exists) {
        if (gui.buttonIcon(@src(), tr("Open in New Window"), gui.entypo.popup, .{}, .{}, .{
            .id_extra = i,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 18, .h = 18 },
            .padding = .all(3),
            .margin = .{ .x = 4 },
        })) {
            m.close();
            ProjectOps.openInNewWindow(path);
        }
    }

    if (gui.buttonIcon(@src(), tr("Remove from Recent Projects"), gui.entypo.trash, .{}, .{}, .{
        .id_extra = i,
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 18, .h = 18 },
        .padding = .all(3),
        .margin = .{ .x = 4 },
    })) {
        removeFromRecent(path);
    }

    if (activated) {
        if (exists and !is_current) {
            m.close();
            ProjectOps.openProject(path);
        } else if (!exists) {
            removeFromRecent(path);
        }
    }
}
