//! Asset browser panel: top-level orchestration only. Picks which view(s) to
//! draw for the current `NavMode`, owns the panel-wide
//! keyboard shortcuts, and the "..." settings menu content (zoom, view
//! mode, Packages visibility). No header/toolbar of its own; the breadcrumb
//! lives in `AssetGridView` instead. The views themselves live in dedicated
//! files so this one stays readable:
//!   - `AssetGridView.zig` — the tile grid (`.grid` / `.grid_tree`)
//!   - `AssetTreeView.zig` — the folder sidebar and the full tree
//!     (`.grid_tree` / `.tree_only`)
//!   - `AssetNav.zig`       — shared current-folder state + delete dialog
//!   - `AssetContextMenus.zig` — shared context-menu content

const std = @import("std");
const gui = @import("gui");
const EditorState = @import("../services/EditorState.zig");
const AssetNav = @import("AssetNav.zig");
const AssetGridView = @import("AssetGridView.zig");
const AssetTreeView = @import("AssetTreeView.zig");
const AssetTileLayout = @import("AssetTileLayout.zig");

/// How the browser lays out folder navigation: the
/// original tile grid, the grid with an added folder-only tree sidebar for
/// navigation, or an exclusive tree (folders + files) replacing the grid
/// entirely — Unity-style.
const NavMode = enum { grid, grid_tree, tree_only };
var g_nav_mode: NavMode = .grid;
var g_browser_split_ratio: f32 = 0.22;

/// Settings key for `g_nav_mode`. Lazily synced on first ready frame
/// (`syncNavModeFromSettings`) because settings aren't loaded when this
/// module's globals initialize — same pattern as `MenuBar.show_editor_fps`.
const NAV_MODE_SETTING_KEY = "asset_browser.nav_mode";
var nav_mode_loaded: bool = false;

fn syncNavModeFromSettings() void {
    if (nav_mode_loaded or !EditorState.settingsReady()) return;
    const raw = EditorState.settings.getString(NAV_MODE_SETTING_KEY, @tagName(NavMode.grid));
    g_nav_mode = std.meta.stringToEnum(NavMode, raw) orelse .grid;
    nav_mode_loaded = true;
}

/// Whether the read-only "Packages" section (`AssetGridView.drawPackagesSection`)
/// is shown at all, not just collapsed — hidden by default since most
/// projects care about their own assets, not installed packages' contents.
var g_show_packages: bool = false;
const SHOW_PACKAGES_SETTING_KEY = "asset_browser.show_packages";
var show_packages_loaded: bool = false;

fn syncShowPackagesFromSettings() void {
    if (show_packages_loaded or !EditorState.settingsReady()) return;
    g_show_packages = EditorState.settings.getBool(SHOW_PACKAGES_SETTING_KEY, false);
    show_packages_loaded = true;
}

/// Read by `AssetGridView` to decide whether to draw the Packages section.
pub fn showPackages() bool {
    syncShowPackagesFromSettings();
    return g_show_packages;
}

var prev_project_path_buf: [1024]u8 = undefined;
var prev_project_path_len: usize = 0;

/// Three-way icon toggle for `NavMode`: plain grid, grid with a folder tree
/// sidebar, or tree-only (folders + files).
///
/// The buttons need a horizontal box of their own — the settings popup lays
/// its children out vertically — and, inside it, no gravity on the layout
/// axis: a `gravity_x` centers a box child across the box's *whole* width
/// rather than packing it after its predecessor, which stacks all three at the
/// same x and leaves only the last one visible. Centering is done with spacers
/// instead.
fn drawNavModeToggle() void {
    const modes = [_]struct { mode: NavMode, icon: []const u8, tip: []const u8 }{
        .{ .mode = .grid, .icon = gui.entypo.grid, .tip = "Grid" },
        .{ .mode = .grid_tree, .icon = gui.entypo.flow_tree, .tip = "Grid + Folder Tree" },
        .{ .mode = .tree_only, .icon = gui.entypo.tree, .tip = "Tree" },
    };

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    _ = gui.spacer(@src(), .{ .expand = .horizontal });
    for (modes, 0..) |m, i| {
        if (gui.buttonIcon(@src(), m.tip, m.icon, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 20, .h = 20 },
            .padding = .all(4),
            .margin = .{ .x = 2 },
            .id_extra = i,
            .style = if (g_nav_mode == m.mode) .highlight else .control,
        })) {
            g_nav_mode = m.mode;
            if (EditorState.settingsReady()) {
                EditorState.settings.setString(NAV_MODE_SETTING_KEY, @tagName(m.mode)) catch {};
            }
        }
    }
    _ = gui.spacer(@src(), .{ .expand = .horizontal });
}

/// Draws the dock header's "..." settings menu: the tile-size zoom slider,
/// the view-mode toggle, and the Packages-section visibility —
/// infrequently-touched options, exactly what that menu is for.
pub fn drawSettings(instance_id: []const u8) void {
    _ = instance_id;
    syncShowPackagesFromSettings();

    _ = gui.sliderEntry(@src(), "{d:0.0}", .{
        .value = &AssetTileLayout.tile_content,
        .min = AssetTileLayout.TILE_CONTENT_MIN,
        .max = AssetTileLayout.TILE_CONTENT_MAX,
        .interval = 1,
        .label = "Size",
    }, .{ .expand = .horizontal, .min_size_content = .{ .w = 150 } });

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
    drawNavModeToggle();

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
    if (gui.checkbox(@src(), &g_show_packages, "Show Packages", .{ .expand = .horizontal })) {
        if (EditorState.settingsReady()) {
            EditorState.settings.setBool(SHOW_PACKAGES_SETTING_KEY, g_show_packages) catch {};
        }
    }
}

/// Draw the asset browser panel with file tiles and navigation.
pub fn draw() void {
    syncNavModeFromSettings();

    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .app1,
    });
    defer outer.deinit();

    const cur_path_slice = EditorState.project_path orelse "";
    const prev_path_slice = prev_project_path_buf[0..prev_project_path_len];
    if (!std.mem.eql(u8, cur_path_slice, prev_path_slice)) {
        AssetNav.setCurrentSubdir("");
        const n = @min(cur_path_slice.len, prev_project_path_buf.len);
        @memcpy(prev_project_path_buf[0..n], cur_path_slice[0..n]);
        prev_project_path_len = n;
    }

    const proj_path = EditorState.project_path orelse {
        var center = gui.box(@src(), .{}, .{
            .expand = .both,
            .gravity_x = 0.5,
            .gravity_y = 0.5,
        });
        defer center.deinit();
        return;
    };

    var assets_path_buf: [1024]u8 = undefined;
    const assets_path = std.fmt.bufPrint(&assets_path_buf, "{s}/assets", .{proj_path}) catch {
        gui.label(@src(), "Path too long.", .{}, .{});
        return;
    };

    // Reveal-in-browser request (e.g. from double-clicking an asset reference
    // in the inspector): navigate to the asset's folder before listing.
    if (EditorState.takeRevealRequest()) |rp| AssetNav.revealTo(assets_path, rp);

    var browse_path_buf: [1024]u8 = undefined;
    const browse_path: []const u8 = if (AssetNav.current_subdir_len > 0)
        std.fmt.bufPrint(&browse_path_buf, "{s}/{s}", .{ assets_path, AssetNav.currentSubdir() }) catch assets_path
    else
        assets_path;

    // Publish the current folder so folder-agnostic actions (e.g. "Create
    // Prefab" from the Scene Tree) write into the folder on screen.
    EditorState.setActiveBrowseDir(browse_path);

    // Handle keyboard events. Only meaningful while grid tiles are on screen —
    // in tree-only mode the tree itself owns arrow/F2/Delete via `TreeView`.
    if (g_nav_mode != .tree_only) {
        for (gui.events()) |*e| {
            if (e.handled) continue;
            if (e.evt != .key) continue;
            const ke = e.evt.key;
            if (ke.action != .down and ke.action != .repeat) continue;
            const mod = ke.mod;

            if (EditorState.isRenaming()) {
                if (ke.action == .down and ke.code == .escape) {
                    e.handle(@src(), outer.data());
                    EditorState.cancelRename();
                }
                continue;
            }

            if (ke.code == .up or ke.code == .down or ke.code == .left or ke.code == .right) {
                e.handle(@src(), outer.data());
                AssetNav.navigateBrowserItems(ke.code == .up or ke.code == .left, browse_path);
                continue;
            }

            if (ke.action != .down) continue;

            if (mod.control() and ke.code == .c and EditorState.selected_asset_path != null) {
                e.handle(@src(), outer.data());
                gui.clipboardTextSet(EditorState.selected_asset_path.?);
            } else if (ke.code == .f2 and EditorState.selected_asset_path != null) {
                e.handle(@src(), outer.data());
                EditorState.startRenameAsset(EditorState.selected_asset_path.?);
            }
        }
    }

    // Handle delete confirmation dialog (shared by the grid tiles and both
    // tree views — all three route deletes through the same pending-path +
    // dialog state).
    AssetNav.handleDeleteDialog();

    switch (g_nav_mode) {
        .tree_only => AssetTreeView.drawFullTree(outer.data()),
        .grid => AssetGridView.draw(proj_path, browse_path, outer.data(), true),
        .grid_tree => {
            var split = gui.paned(@src(), .{
                .direction = .horizontal,
                .collapsed_size = 0,
                .handle_margin = 4,
                .split_ratio = &g_browser_split_ratio,
            }, .{ .expand = .both });
            defer split.deinit();
            if (split.showFirst()) AssetTreeView.drawFolderSidebar();
            // `..` is redundant once folder navigation flows through the
            // sidebar tree .
            if (split.showSecond()) AssetGridView.draw(proj_path, browse_path, outer.data(), false);
        },
    }
}
