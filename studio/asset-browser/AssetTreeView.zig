//! Both asset-browser tree presentations in one file, since the Grid+Tree
//! folder sidebar (issue #80) really is the full tree (issues #79/#83) with
//! files filtered out — one comptime-parameterized `TreeView` model
//! (`Model(files_included)`) backs both, differing only where folder-only
//! and folder+file behavior genuinely diverge (selection target, activation,
//! row icon, context menu). Both project `AssetTree`'s flat recursive scan
//! through the shared `TreeView` machinery (rename, delete, drag-reparent,
//! context menus, keyboard nav) — the same pattern `SceneTree.zig` uses over
//! `EditorState.objects`.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const AssetActions = @import("AssetActions.zig");
const PreviewSystem = @import("preview/PreviewSystem.zig");
const AssetTree = @import("AssetTree.zig");
const AssetNav = @import("AssetNav.zig");
const AssetContextMenus = @import("AssetContextMenus.zig");
const tree_view = @import("../TreeView.zig");

/// Navigate the grid to folder node `node_idx` — strips `AssetTree`'s
/// `assets`-root prefix off its stored path to get the subdir-relative form
/// `AssetNav` holds.
fn navigateToFolderNode(node_idx: usize) void {
    const full = AssetTree.path(node_idx);
    const root = AssetTree.rootPath();
    var rel = full;
    if (std.mem.startsWith(u8, full, root)) {
        rel = full[root.len..];
        if (rel.len > 0 and rel[0] == '/') rel = rel[1..];
    }
    AssetNav.setCurrentSubdir(rel);
}

/// `AssetTree` node index of the folder currently on screen in the grid, or
/// null at the assets root (which has no explicit node of its own).
fn currentFolderNodeIdx() ?usize {
    if (AssetNav.current_subdir_len == 0) return null;
    var buf: [1024]u8 = undefined;
    const full = std.fmt.bufPrint(&buf, "{s}/{s}", .{ AssetTree.rootPath(), AssetNav.currentSubdir() }) catch return null;
    return AssetTree.indexOfPath(full);
}

/// `TreeView` model over `AssetTree`. `files_included = false` projects only
/// directories (folder-local indices via `AssetTree.folderNode`/
/// `folderParentOf`, the Grid+Tree sidebar); `files_included = true` walks
/// every node directly (raw `AssetTree` indices, Tree Only mode). `node(i)`
/// is the one place that distinction lives — everything else reads through
/// it, so the two modes can't drift apart.
fn Model(comptime files_included: bool) type {
    return struct {
        /// Selected row, cached once per draw (`syncPrimary`) so
        /// `isPrimary`/`isSelected` are O(1) instead of re-deriving the
        /// selection (an `AssetTree.indexOfPath` linear scan) per row per
        /// frame. Each `files_included` instantiation gets its own copy —
        /// the folder sidebar and full tree track selection independently.
        var primary: ?usize = null;

        fn node(i: usize) usize {
            return if (files_included) i else AssetTree.folderNode(i);
        }

        /// Refresh `primary` for the current frame. Call once before
        /// `TreeView(Model).draw`.
        pub fn syncPrimary() void {
            primary = if (files_included)
                (if (EditorState.selected_asset_path) |p| AssetTree.indexOfPath(p) else null)
            else if (currentFolderNodeIdx()) |folder_node| blk: {
                const fi = AssetTree.folderIndexOfNode(folder_node);
                break :blk if (fi >= 0) @as(usize, @intCast(fi)) else null;
            } else null;
        }

        pub fn count() usize {
            return if (files_included) AssetTree.count() else AssetTree.folderCount();
        }
        pub fn parentOf(i: usize) i32 {
            return if (files_included) AssetTree.parentOf(i) else AssetTree.folderParentOf(i);
        }
        pub fn name(i: usize) []const u8 {
            return AssetTree.name(node(i));
        }
        pub fn isSelected(i: usize) bool {
            return isPrimary(i);
        }
        pub fn isPrimary(i: usize) bool {
            return primary != null and primary.? == i;
        }
        pub fn primarySelection() ?usize {
            return primary;
        }
        pub fn select(i: usize, _: tree_view.Mods) void {
            if (files_included) {
                EditorState.selectAsset(AssetTree.path(i));
            } else {
                navigateToFolderNode(node(i));
            }
        }
        /// Double-click: open the file. No-op for the folder-only sidebar —
        /// a single click already navigates, there's nothing further to do.
        pub fn activate(i: usize) void {
            if (!files_included) return;
            if (AssetTree.isDir(i)) return;
            const desc = editor.asset_registry.get(AssetTree.assetType(i));
            if (desc.open_mode == .none) return;
            AssetContextMenus.openAsset(AssetTree.dirOf(i), AssetTree.name(i), desc.open_mode);
        }
        pub fn applyRename(i: usize, new_name: []const u8) void {
            EditorState.startRenameAsset(AssetTree.path(node(i)));
            const n = @min(new_name.len, EditorState.g_rename.buf.len);
            @memcpy(EditorState.g_rename.buf[0..n], new_name[0..n]);
            EditorState.g_rename.len = n;
            EditorState.commitRename(gui.frameTimeNS(), gui.io);
        }
        pub fn reparent(drag: usize, target: usize, zone: tree_view.DropZone) void {
            const drag_path = AssetTree.path(node(drag));
            const target_node = node(target);
            // A folder-tree target is always a directory; a full-tree target
            // might be a file, so dropping "into" it really means "beside it".
            const drop_into_dir = zone == .into and (!files_included or AssetTree.isDir(target_node));
            const dest_dir = if (drop_into_dir) AssetTree.path(target_node) else AssetTree.dirOf(target_node);
            AssetActions.moveAsset(drag_path, dest_dir);
        }
        pub fn removeRequested() void {
            const idx = primary orelse return;
            AssetNav.requestDelete(AssetTree.path(node(idx)));
        }
        pub fn onDragStart(i: usize) void {
            EditorState.startDragAsset(AssetTree.path(node(i)));
        }
        pub fn rowIcon(i: usize, has_children: bool) tree_view.RowIcon {
            _ = has_children;
            if (!files_included) return .{ .bytes = gui.entypo.folder };
            if (AssetTree.isDir(i)) return .{ .bytes = gui.entypo.folder };
            if (PreviewSystem.imageSourceFor(AssetTree.path(i))) |source| return .{ .image = source };
            const desc = editor.asset_registry.get(AssetTree.assetType(i));
            return .{ .bytes = AssetContextMenus.iconForHint(desc.icon_hint) };
        }
        pub fn contextItems(i: usize, fw: *gui.FloatingMenuWidget) void {
            const idx = node(i);
            if (!files_included) {
                _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 5000 + i });
                AssetContextMenus.drawCreateAssetMenuItems(fw, AssetTree.path(idx), 6000 + i * 100);
                return;
            }
            const proj_path = EditorState.project_path orelse return;
            AssetContextMenus.drawAssetExtraMenuItems(fw, proj_path, AssetTree.dirOf(idx), AssetTree.name(idx), AssetTree.isDir(idx), AssetTree.assetType(idx), 8000 + i);
            if (AssetTree.isDir(idx)) {
                _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 9000 + i });
                AssetContextMenus.drawCreateAssetMenuItems(fw, AssetTree.path(idx), 10000 + i * 100);
            }
        }
    };
}

const FolderTree = tree_view.TreeView(Model(false));
const FullTree = tree_view.TreeView(Model(true));

/// Permanent `assets` row pinned above the folder tree, always visible and
/// always clickable — without it, once a user descends into a subfolder
/// there is no way back to the root (the tree has no node for the root
/// itself, and the `..` breadcrumb button is hidden whenever this sidebar is
/// shown, since the tree is meant to replace it).
fn drawRootRow() void {
    const is_root = AssetNav.current_subdir_len == 0;
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .background = is_root,
        .style = if (is_root) .highlight else .window,
        .padding = .all(4),
    });
    defer row.deinit();

    for (gui.events()) |*e| {
        if (!gui.eventMatchSimple(e, row.data())) continue;
        if (e.evt == .mouse) {
            const me = e.evt.mouse;
            if (me.action == .press and me.button == .left) {
                e.handle(@src(), row.data());
                AssetNav.setCurrentSubdir("");
            }
        }
    }

    gui.icon(@src(), "root_icon", gui.entypo.folder, .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 16, .h = 16 },
    });
    gui.label(@src(), "assets", .{}, .{ .gravity_y = 0.5 });
}

/// Grid+Tree sidebar (issue #80): folders only, clicking navigates the grid
/// pane drawn alongside it.
pub fn drawFolderSidebar() void {
    AssetTree.ensure(gui.io);
    Model(false).syncPrimary();

    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
    defer scroll.deinit();

    drawRootRow();
    FolderTree.draw(outer.data());

    // Empty-area context menu: create at the assets root.
    if (FolderTree.dragging_idx == null) {
        const cxt = gui.context(@src(), .{ .rect = outer.data().borderRectScale().r }, .{});
        defer cxt.deinit();
        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{});
            defer fw.deinit();
            AssetContextMenus.drawCreateAssetMenuItems(fw, AssetTree.rootPath(), 0);
        }
    }
}

/// Tree Only mode (issues #79/#83): folders + files in one tree, replacing
/// the grid entirely. Selecting a file routes through the same
/// `EditorState.selected_asset_path` the grid uses, so the Inspector shows
/// its preview exactly as it does for a grid tile, and rows show the same
/// `PreviewSystem` thumbnails the grid tiles do (row-sized).
pub fn drawFullTree(outer_wd: *gui.WidgetData) void {
    AssetTree.ensure(gui.io);
    Model(true).syncPrimary();

    var scroll = gui.scrollArea(@src(), .{ .vertical = .auto }, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
    defer scroll.deinit();

    FullTree.draw(outer_wd);

    // Empty-area context menu: create at the assets root.
    if (FullTree.dragging_idx == null) {
        const cxt = gui.context(@src(), .{ .rect = outer_wd.borderRectScale().r }, .{});
        defer cxt.deinit();
        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{});
            defer fw.deinit();
            AssetContextMenus.drawCreateAssetMenuItems(fw, AssetTree.rootPath(), 0);
        }
    }
}
