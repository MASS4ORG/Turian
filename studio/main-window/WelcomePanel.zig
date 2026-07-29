//! Welcome panel: the default view when Studio opens with no project loaded
//! (see `LayoutStore.setProjectOpen`) — recent projects on the left, quick
//! actions and documentation/community links on the right. Also addable as a
//! normal dockable tab via View ▸ Add Welcome once a project is open.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("editor").EditorState;
const ExternalEditor = @import("editor").external_editor;
const ProjectOps = @import("../services/ProjectOps.zig");
const RecentProjectInfo = @import("../services/RecentProjectInfo.zig");
const Icon = @import("../Icon.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const build_options = @import("turian_build_options");
const tr = StudioLocale.tr;

const DOCS_URL = "https://turian.mass4.org/docs/";
const BLOG_URL = "https://turian.mass4.org/blog";
const CHANGELOG_URL = "https://gitlab.com/mass4org/mega4/turian/-/blob/main/CHANGELOG.md";
const DISCORD_URL = "https://discord.com/channels/1104509879269457982/1384499574281867274";
const MATRIX_URL = "https://matrix.to/#/!vRaFlDqBZyMXNRKDch:matrix.org";
const ISSUES_URL = "https://gitlab.com/mass4org/mega4/turian/-/issues";

pub fn draw() void {
    var outer = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .both, .background = true, .style = .app1 });
    defer outer.deinit();

    drawRecentColumn();
    _ = gui.separator(@src(), .{ .expand = .vertical, .margin = gui.Rect.all(4) });
    drawActionsColumn();
}

/// Left column: recent projects, most-recently-opened first (see
/// `editor.recent_projects`), each clickable to reopen.
fn drawRecentColumn() void {
    var col = gui.box(@src(), .{}, .{ .expand = .both, .padding = gui.Rect.all(16) });
    defer col.deinit();

    gui.label(@src(), "{s}", .{tr("Recent Projects")}, .{ .font = .theme(.heading) });
    _ = gui.spacer(@src(), .{ .min_size_content = .{ .h = 8 } });

    if (!EditorState.settingsReady()) {
        gui.label(@src(), "{s}", .{tr("Settings not ready")}, .{});
        return;
    }

    var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both });
    defer scroll.deinit();

    const arena = gui.currentWindow().arena();
    const recent = editor.recent_projects.list(&EditorState.settings, arena);
    if (recent.len == 0) {
        gui.label(@src(), "{s}", .{tr("No recent projects")}, .{});
        return;
    }
    for (recent, 0..) |path, i| drawRecentRow(path, i);
}

fn projectDirExists(path: []const u8) bool {
    var d = std.Io.Dir.cwd().openDir(gui.io, path, .{}) catch return false;
    d.close(gui.io);
    return true;
}

fn drawIcon(src: gui.ImageSource) void {
    _ = gui.image(@src(), .{ .source = src, .shrink = .ratio }, .{
        .min_size_content = .{ .w = 16, .h = 16 },
        .gravity_y = 0.5,
        .margin = .{ .w = 4 },
    });
}

fn removeFromRecent(path: []const u8) void {
    const arena = gui.currentWindow().arena();
    editor.recent_projects.remove(&EditorState.settings, gui.io, arena, path);
    EditorState.settings.save(gui.io);
}

/// Trailing width (natural px) reserved for the remove button, kept out of
/// the row button's own click rect — see the comment inside `drawRecentRow`.
const REMOVE_SLOT: f32 = 32;

/// One recent-project row: icon + resolved project name (bold) over its full
/// path (dim), clicking opens it — matches `ProjectDropdown`'s presentation.
/// A missing directory shows a "[!]" marker; clicking it prunes the stale
/// entry instead of trying to open it.
fn drawRecentRow(path: []const u8, i: usize) void {
    const is_current = if (EditorState.project_path) |cur| std.mem.eql(u8, cur, path) else false;
    const exists = projectDirExists(path);
    const e = RecentProjectInfo.info(gui.io, path);
    const name = e.displayName();
    const theme = gui.themeGet();

    // An overlay, not a horizontal box: the row button and remove button
    // both need the same full-width footprint (the button so its highlight
    // spans the row, the remove button so it sits in the top-right corner
    // on top of that highlight) rather than being laid out side by side.
    var stack = gui.overlay(@src(), .{ .expand = .horizontal, .id_extra = i });
    defer stack.deinit();

    var bw: gui.ButtonWidget = undefined;
    bw.init(@src(), .{}, .{
        .expand = .horizontal,
        .id_extra = i,
        .margin = gui.Rect.all(2),
        .style = if (is_current) .highlight else .control,
    });
    // Clip the click rect so the overlaid remove button receives its own clicks.
    var click_rect = bw.data().borderRectScale().r;
    click_rect.w = @max(0, click_rect.w - REMOVE_SLOT * gui.windowNaturalScale());
    bw.click = gui.clicked(bw.data(), .{ .rect = click_rect, .hovered = &bw.hover });
    bw.drawBackground();
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .id_extra = i, .gravity_y = 0.5 });
        defer row.deinit();
        if (RecentProjectInfo.iconSource(e)) |src| drawIcon(src);
        var col = gui.box(@src(), .{}, .{ .id_extra = i });
        defer col.deinit();

        const arena = gui.currentWindow().arena();
        const title = if (!exists)
            std.fmt.allocPrint(arena, "[!] {s}", .{name}) catch name
        else
            name;
        gui.labelNoFmt(@src(), title, .{}, .{ .font = .theme(.body), .id_extra = i });
        gui.labelNoFmt(@src(), path, .{}, .{
            .id_extra = i,
            .color_text = theme.color(.window, .text).opacity(0.6),
        });
    }
    const clicked = bw.click;
    bw.drawFocus();
    bw.deinit();

    if (gui.buttonIcon(@src(), tr("Remove from Recent Projects"), gui.entypo.cross, .{}, .{}, .{
        .id_extra = i,
        .gravity_x = 1.0,
        .gravity_y = 0.0,
        .min_size_content = .{ .w = 18, .h = 18 },
        .padding = .all(3),
        .margin = .{ .x = 6, .y = 6 },
    })) {
        removeFromRecent(path);
    }

    if (clicked) {
        if (exists and !is_current) {
            ProjectOps.openProject(path);
        } else if (!exists) {
            removeFromRecent(path);
        }
    }
}

/// Right column: logo, version, quick actions, and documentation/community
/// links.
fn drawActionsColumn() void {
    var col = gui.box(@src(), .{}, .{ .min_size_content = .{ .w = 260 }, .padding = gui.Rect.all(16) });
    defer col.deinit();

    _ = gui.image(@src(), .{
        .source = .{ .imageFile = .{ .bytes = Icon.png, .name = "turian_icon" } },
        .shrink = .ratio,
    }, .{ .min_size_content = .{ .w = 64, .h = 64 }, .gravity_x = 0.5, .margin = gui.Rect.all(4) });

    gui.label(@src(), "{s}", .{tr("Turian Studio")}, .{ .font = .theme(.title), .gravity_x = 0.5 });
    gui.label(@src(), "v{s}", .{build_options.version}, .{ .gravity_x = 0.5 });

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .h = 12 } });

    if (gui.button(@src(), tr("New Project..."), .{}, .{ .expand = .horizontal })) {
        ProjectOps.newProjectDialog();
    }
    if (gui.button(@src(), tr("Open Project..."), .{}, .{ .expand = .horizontal })) {
        ProjectOps.openProjectDialog();
    }

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .h = 12 } });
    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
    _ = gui.spacer(@src(), .{ .min_size_content = .{ .h = 4 } });

    gui.label(@src(), "{s}", .{tr("Documentation & Community")}, .{ .font = .theme(.heading) });
    drawLinkButton(tr("Documentation"), DOCS_URL, 1);
    drawLinkButton(tr("Blog"), BLOG_URL, 2);
    drawLinkButton(tr("Changelog"), CHANGELOG_URL, 3);
    drawLinkButton(tr("Discord"), DISCORD_URL, 4);
    drawLinkButton(tr("Matrix"), MATRIX_URL, 5);
    drawLinkButton(tr("Issues"), ISSUES_URL, 6);
}

fn drawLinkButton(label: []const u8, url: []const u8, id_extra: usize) void {
    if (gui.button(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = id_extra })) {
        ExternalEditor.openUrl(gui.io, url);
    }
}
