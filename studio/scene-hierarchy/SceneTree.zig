//! Scene hierarchy panel. Row rendering, selection, rename, keyboard nav,
//! context menus and drag-reparenting all live in the shared `TreeView`
//! machinery (C1) — this file provides the `EditorState`-backed model plus
//! scene-specific chrome (delete confirmation dialog, background create
//! menu, prefab visuals, multi-select semantics).

const gui = @import("gui");
const EditorState = @import("../services/EditorState.zig");
const tree_view = @import("../TreeView.zig");

var g_show_delete_dialog: bool = false;
var g_delete_dialog_result: ?bool = null;

const prefab_tint = gui.Color{ .r = 90, .g = 165, .b = 245, .a = 255 };

/// `TreeView` model over `EditorState`'s flat parent-indexed scene objects.
const SceneModel = struct {
    pub fn count() usize {
        return EditorState.object_count;
    }

    pub fn parentOf(i: usize) i32 {
        return EditorState.objects[i].parent;
    }

    pub fn name(i: usize) []const u8 {
        return EditorState.objects[i].nameSlice();
    }

    pub fn isSelected(i: usize) bool {
        return EditorState.isObjectSelected(i);
    }

    pub fn isPrimary(i: usize) bool {
        return EditorState.selected_object != null and EditorState.selected_object.? == i;
    }

    pub fn primarySelection() ?usize {
        return EditorState.selected_object;
    }

    pub fn select(i: usize, mods: tree_view.Mods) void {
        if (mods.shift) {
            EditorState.selected_object = i;
            if (EditorState.last_select_idx) |last_idx| {
                EditorState.clearSelectedObjects();
                EditorState.selectObjectRange(last_idx, i);
            } else {
                EditorState.clearSelectedObjects();
                EditorState.selectObject(i);
            }
            EditorState.selectObject(i);
        } else if (mods.ctrl) {
            EditorState.selected_object = i;
            EditorState.toggleSelectObject(i);
            EditorState.selectObject(i);
        } else {
            EditorState.clearSelectedObjects();
            EditorState.selected_object = i;
            EditorState.selectObject(i);
        }
    }

    pub fn activate(i: usize) void {
        EditorState.focusOnObject(i);
    }

    pub fn applyRename(i: usize, new_name: []const u8) void {
        // Route through EditorState's rename machinery so the change lands
        // in the undo stack exactly like before the TreeView extraction.
        EditorState.startRenameObject(i);
        const n = @min(new_name.len, EditorState.g_rename.buf.len);
        @memcpy(EditorState.g_rename.buf[0..n], new_name[0..n]);
        EditorState.g_rename.len = n;
        EditorState.commitRename(gui.frameTimeNS(), gui.io);
    }

    pub fn reparent(drag: usize, target: usize, zone: tree_view.DropZone) void {
        if (target >= EditorState.object_count) return;
        const now = gui.frameTimeNS();
        const t_parent = EditorState.objects[target].parent;
        switch (zone) {
            .into => EditorState.reparentObject(now, drag, @intCast(target), -1),
            .before => EditorState.reparentObject(now, drag, t_parent, @intCast(target)),
            .after => EditorState.reparentObject(now, drag, t_parent, nextSibling(target)),
        }
    }

    pub fn removeRequested() void {
        if (EditorState.selectedCount() > 0 or EditorState.selected_object != null) {
            g_show_delete_dialog = true;
        }
    }

    pub fn onDragStart(i: usize) void {
        EditorState.startDragObject(i);
    }

    pub fn rowIcon(i: usize, has_children: bool) tree_view.RowIcon {
        const obj = &EditorState.objects[i];
        // Prefab instances are tinted blue and use a box icon, so a linked
        // instance is distinguishable at a glance from a plain object.
        const bytes = if (obj.isPrefabInstanceRoot())
            gui.entypo.box
        else if (has_children)
            gui.entypo.folder
        else
            gui.entypo.text_document;
        return .{
            .bytes = bytes,
            .tint = if (obj.isPartOfPrefab()) prefab_tint else null,
        };
    }

    pub fn contextItems(idx: usize, fw: *gui.FloatingMenuWidget) void {
        if (gui.menuItemLabel(@src(), "Duplicate", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            if (EditorState.selectedCount() > 1 and EditorState.isObjectSelected(idx)) {
                EditorState.duplicateSelectedObjects(gui.frameTimeNS(), gui.io);
            } else {
                EditorState.duplicateObject(gui.frameTimeNS(), gui.io, idx);
            }
        }

        // Scene-wide creation, reachable from any node's menu (so it's
        // available even when the hierarchy is full and has no empty space).
        if (gui.menuItemLabel(@src(), "Create Empty Child", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            const ni = EditorState.addObjectWithUndo(gui.frameTimeNS(), gui.io, "New Object", @intCast(idx));
            EditorState.clearSelectedObjects();
            EditorState.selected_object = ni;
            EditorState.selectObject(ni);
        }
        if (gui.menuItemLabel(@src(), "Create Empty", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            const ni = EditorState.addObjectWithUndo(gui.frameTimeNS(), gui.io, "New Object", EditorState.objects[idx].parent);
            EditorState.clearSelectedObjects();
            EditorState.selected_object = ni;
            EditorState.selectObject(ni);
        }

        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 200 + idx });

        if (gui.menuItemLabel(@src(), "Create Prefab", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            _ = EditorState.createPrefabFromObject(gui.frameTimeNS(), gui.io, idx);
        }

        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 100 + idx });

        if (gui.menuItemLabel(@src(), "Copy GUID", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            const gs = EditorState.objects[idx].guidSlice();
            if (gs.len > 0) gui.clipboardTextSet(gs);
        }

        if (gui.menuItemLabel(@src(), "Frame Object", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            EditorState.focusOnObject(idx);
        }
    }
};

const Tree = tree_view.TreeView(SceneModel);

/// Array index of the sibling immediately after `idx` (same parent), or -1.
fn nextSibling(idx: usize) i32 {
    const p = EditorState.objects[idx].parent;
    var i = idx + 1;
    while (i < EditorState.object_count) : (i += 1) {
        if (EditorState.objects[i].parent == p) return @intCast(i);
    }
    return -1;
}

/// Draw the scene hierarchy tree with drag-and-drop reordering.
pub fn draw() void {
    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .app1,
    });
    defer outer.deinit();

    {
        var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both, .style = .app1, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
        defer scroll.deinit();

        handleDeleteDialog();
        Tree.draw(outer.data());
    }

    // Empty-area context menu: create objects (right-click the panel). Only
    // meaningful when a scene/prefab is open, and never while a node is being
    // dragged (so it can't interfere with drag-reordering / reparenting).
    if (EditorState.hasOpenScene() and Tree.dragging_idx == null) drawBackgroundMenu(outer.data());
}

/// Right-click context menu for the hierarchy background: create scene objects.
fn drawBackgroundMenu(wd: *gui.WidgetData) void {
    const cxt = gui.context(@src(), .{ .rect = wd.borderRectScale().r }, .{});
    defer cxt.deinit();

    if (cxt.activePoint()) |cp| {
        var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{});
        defer fw.deinit();

        if (gui.menuItemLabel(@src(), "Create Empty", .{}, .{ .expand = .horizontal }) != null) {
            fw.close();
            const idx = EditorState.addObjectWithUndo(gui.frameTimeNS(), gui.io, "New Object", -1);
            EditorState.clearSelectedObjects();
            EditorState.selected_object = idx;
            EditorState.selectObject(idx);
        }

        if (EditorState.selected_object) |sel| {
            if (gui.menuItemLabel(@src(), "Create Empty Child", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                const idx = EditorState.addObjectWithUndo(gui.frameTimeNS(), gui.io, "New Object", @intCast(sel));
                EditorState.clearSelectedObjects();
                EditorState.selected_object = idx;
                EditorState.selectObject(idx);
            }
        }
    }
}

fn handleDeleteDialog() void {
    {
        const result = &g_delete_dialog_result;
        if (result.*) |confirmed| {
            g_show_delete_dialog = false;
            result.* = null;
            if (confirmed) {
                EditorState.deleteSelectedObjects(gui.frameTimeNS());
            }
        }
    }

    if (!g_show_delete_dialog) return;
    _ = EditorState.selected_object orelse {
        g_show_delete_dialog = false;
        return;
    };

    gui.dialog(@src(), .{}, .{
        .title = "Delete Object",
        .message = "Delete selected object and all its children?",
        .ok_label = "Delete",
        .cancel_label = "Cancel",
        .default = .cancel,
        .callafterFn = struct {
            fn callafter(_: gui.Id, response: gui.enums.DialogResponse) !void {
                g_delete_dialog_result = response == .ok;
            }
        }.callafter,
    });
}
