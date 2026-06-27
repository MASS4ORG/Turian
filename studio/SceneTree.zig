const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

var g_dragging_idx: ?usize = null;

/// Where the dragged node will land relative to the hovered target row.
const DropZone = enum { before, into, after };
var g_drop_target: ?usize = null;
var g_drop_zone: DropZone = .into;

var g_last_click_idx: ?usize = null;
var g_last_click_ns: i128 = 0;

var g_show_delete_dialog: bool = false;
var g_delete_dialog_result: ?bool = null;

/// Draw the scene hierarchy tree with drag-and-drop reordering.
pub fn draw() void {
    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    {
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();
        gui.label(@src(), "Scene Hierarchy", .{}, .{ .font = .theme(.heading) });
    }

    {
        var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
        defer scroll.deinit();

        if (EditorState.object_count == 0) {
            // Blank state is cleaner than a placeholder message.
            if (EditorState.isRenaming()) EditorState.cancelRename();
        } else {
            handleKeyboard(outer.data());

            handleDeleteDialog();

            var tree = gui.TreeWidget.tree(@src(), .{ .enable_reordering = true }, .{ .expand = .horizontal });
            defer tree.deinit();

            var had_removed: bool = false;

            // Recomputed each frame while a node is dragged (see renderNode).
            g_drop_target = null;
            renderLevel(tree, -1, 0, &had_removed);

            // On drop, reparent/reorder the dragged node based on the hovered
            // drop zone (into / before / after). See applyDrop.
            if (had_removed) {
                if (g_dragging_idx) |di| {
                    if (g_drop_target) |tgt| applyDrop(di, tgt, g_drop_zone);
                }
                g_dragging_idx = null;
                g_drop_target = null;
            }
        }
    }

    // Empty-area context menu: create objects (right-click the panel). Only
    // meaningful when a scene/prefab is open, and never while a node is being
    // dragged (so it can't interfere with drag-reordering / reparenting).
    if (EditorState.hasOpenScene() and g_dragging_idx == null) drawBackgroundMenu(outer.data());
}

/// Highlight the hovered drop target: a line at the row's top/bottom edge for a
/// sibling drop, or a translucent fill across the row for a child ("into") drop.
fn drawDropIndicator(row: gui.Rect.Physical, zone: DropZone) void {
    const line = gui.Color{ .r = 90, .g = 165, .b = 245, .a = 255 };
    const fill = gui.Color{ .r = 90, .g = 165, .b = 245, .a = 70 };
    switch (zone) {
        .before => {
            var r = row;
            r.h = 2;
            r.fill(.{}, .{ .color = line });
        },
        .after => {
            var r = row;
            r.y = row.y + row.h - 2;
            r.h = 2;
            r.fill(.{}, .{ .color = line });
        },
        .into => row.fill(.{}, .{ .color = fill }),
    }
}

/// Array index of the sibling immediately after `idx` (same parent), or -1.
fn nextSibling(idx: usize) i32 {
    const p = EditorState.objects[idx].parent;
    var i = idx + 1;
    while (i < EditorState.object_count) : (i += 1) {
        if (EditorState.objects[i].parent == p) return @intCast(i);
    }
    return -1;
}

/// Apply a finished drag: reparent / reorder `drag` relative to `target`.
fn applyDrop(drag: usize, target: usize, zone: DropZone) void {
    if (target >= EditorState.object_count) return;
    const now = gui.frameTimeNS();
    const t_parent = EditorState.objects[target].parent;
    switch (zone) {
        .into => EditorState.reparentObject(now, drag, @intCast(target), -1),
        .before => EditorState.reparentObject(now, drag, t_parent, @intCast(target)),
        .after => EditorState.reparentObject(now, drag, t_parent, nextSibling(target)),
    }
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

fn handleKeyboard(root_wd: *gui.WidgetData) void {
    for (gui.events()) |*e| {
        if (e.handled) continue;
        if (e.evt != .key) continue;
        const ke = e.evt.key;
        if (ke.action != .down and ke.action != .repeat) continue;

        if (EditorState.isRenaming()) {
            if (ke.code == .escape) {
                e.handle(@src(), root_wd);
                EditorState.cancelRename();
                return;
            }
            return;
        }

        if (ke.code == .up or ke.code == .down) {
            e.handle(@src(), root_wd);
            navigateSelection(ke.code == .up);
            return;
        }

        if (ke.action != .down) continue;

        if (ke.code == .f2) {
            if (EditorState.selected_object) |sel| {
                e.handle(@src(), root_wd);
                EditorState.startRenameObject(sel);
                return;
            }
        }

        if (ke.code == .delete or ke.code == .backspace) {
            if (EditorState.selectedCount() > 0) {
                e.handle(@src(), root_wd);
                g_show_delete_dialog = true;
                return;
            }
        }
    }
}

fn buildVisualOrder(out: []usize, count: *usize, parent: i32) void {
    for (EditorState.objects[0..EditorState.object_count], 0..) |*obj, i| {
        if (obj.parent == parent) {
            if (count.* < out.len) {
                out[count.*] = i;
                count.* += 1;
            }
            buildVisualOrder(out, count, @intCast(i));
        }
    }
}

fn navigateSelection(go_up: bool) void {
    var visual: [EditorState.MAX_OBJECTS]usize = undefined;
    var count: usize = 0;
    buildVisualOrder(&visual, &count, -1);
    if (count == 0) return;

    const new_idx = blk: {
        if (EditorState.selected_object) |sel| {
            for (visual[0..count], 0..) |v, i| {
                if (v == sel) {
                    if (go_up) {
                        break :blk visual[if (i > 0) i - 1 else 0];
                    } else {
                        break :blk visual[if (i + 1 < count) i + 1 else count - 1];
                    }
                }
            }
        }
        break :blk visual[0];
    };

    EditorState.clearSelectedObjects();
    EditorState.selected_object = new_idx;
    EditorState.selectObject(new_idx);
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

fn renderLevel(tree: *gui.TreeWidget, parent: i32, depth: usize, had_removed: *bool) void {
    for (EditorState.objects[0..EditorState.object_count], 0..) |*obj, i| {
        if (obj.parent == parent) {
            renderNode(tree, i, obj, depth, had_removed);
        }
    }
}

fn renderNode(tree: *gui.TreeWidget, idx: usize, obj: *EditorState.SceneNode, depth: usize, had_removed: *bool) void {
    const has_children = blk: {
        for (EditorState.objects[0..EditorState.object_count]) |*child| {
            if (child.parent == @as(i32, @intCast(idx))) break :blk true;
        }
        break :blk false;
    };

    const is_selected = EditorState.selected_object != null and EditorState.selected_object.? == idx;
    const is_multi_selected = EditorState.isObjectSelected(idx);
    const is_renaming_this = EditorState.isRenaming() and
        EditorState.g_rename.target == .scene_object and
        EditorState.g_rename.idx == idx;

    const branch = tree.branch(@src(), .{ .expanded = depth == 0 }, .{
        .id_extra = idx,
        .expand = .horizontal,
        .background = is_selected or is_multi_selected,
        .style = if (is_selected) .highlight else .window,
    });
    defer branch.deinit();

    if (branch.floating()) {
        g_dragging_idx = idx;
        EditorState.startDragObject(idx);
    }
    if (branch.removed()) had_removed.* = true;

    // While a node is dragged, treat each row as three drop zones — top quarter
    // = drop before (sibling), middle = drop into (make child), bottom quarter =
    // drop after (sibling) — and draw an indicator for the hovered one. The drop
    // itself is applied in draw() once the drag ends. Own subtree is skipped.
    if (g_dragging_idx) |di| {
        if (di != idx and !EditorState.isAncestorOrSelf(idx, @intCast(di))) {
            const row = branch.button.data().borderRectScale().r;
            const mp = gui.currentWindow().mouse_pt;
            if (row.contains(mp)) {
                const rel = if (row.h > 0) (mp.y - row.y) / row.h else 0.5;
                const zone: DropZone = if (rel < 0.25) .before else if (rel > 0.75) .after else .into;
                g_drop_target = idx;
                g_drop_zone = zone;
                drawDropIndicator(row, zone);
            }
        }
    }

    if (branch.button.clicked() and !is_renaming_this) {
        const now = gui.frameTimeNS();

        var ctrl_held = false;
        var shift_held = false;
        for (gui.events()) |*e| {
            if (e.evt == .mouse) {
                const me = e.evt.mouse;
                ctrl_held = me.mod.control();
                shift_held = me.mod.shift();
            }
        }

        const same_idx = g_last_click_idx == idx;
        if (same_idx and now - g_last_click_ns < 500 * std.time.ns_per_ms) {
            g_last_click_idx = null;
            EditorState.focusOnObject(idx);
        } else {
            if (shift_held) {
                EditorState.selected_object = idx;
                if (EditorState.last_select_idx) |last_idx| {
                    EditorState.clearSelectedObjects();
                    EditorState.selectObjectRange(last_idx, idx);
                } else {
                    EditorState.clearSelectedObjects();
                    EditorState.selectObject(idx);
                }
                EditorState.selectObject(idx);
            } else if (ctrl_held) {
                EditorState.selected_object = idx;
                EditorState.toggleSelectObject(idx);
                EditorState.selectObject(idx);
            } else {
                EditorState.clearSelectedObjects();
                EditorState.selected_object = idx;
                EditorState.selectObject(idx);
            }

            g_last_click_idx = idx;
            g_last_click_ns = now;
        }
    }

    // Prefab instances are tinted blue and use a box icon, so a linked instance
    // is distinguishable at a glance from a plain object (issue #32).
    const is_prefab_root = obj.isPrefabInstanceRoot();
    const is_prefab_part = obj.isPartOfPrefab();
    const prefab_tint = gui.Color{ .r = 90, .g = 165, .b = 245, .a = 255 };

    const icon_bytes = if (is_prefab_root)
        gui.entypo.box
    else if (has_children)
        gui.entypo.folder
    else
        gui.entypo.text_document;
    gui.icon(@src(), "icon", icon_bytes, .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 16, .h = 16 },
        .id_extra = idx,
        .color_text = if (is_prefab_part) prefab_tint else null,
    });

    if (is_renaming_this) {
        var te = gui.textEntry(@src(), .{
            .text = .{ .buffer = EditorState.g_rename.buf[0..] },
            .placeholder = "Name",
        }, .{
            .expand = .horizontal,
            .gravity_y = 0.5,
            .id_extra = idx,
            .min_size_content = .{ .w = 100, .h = 22 },
        });
        defer te.deinit();

        // Grab keyboard focus on the first frame so the field is editable.
        if (EditorState.g_rename.just_started) {
            gui.focusWidget(te.data().id, null, null);
            EditorState.g_rename.just_started = false;
        }

        if (te.enter_pressed) {
            const text = te.textGet();
            EditorState.g_rename.len = text.len;
            EditorState.commitRename(gui.frameTimeNS(), gui.io);
        }
    } else {
        gui.label(@src(), "{s}", .{obj.nameSlice()}, .{
            .gravity_y = 0.5,
            .id_extra = idx,
            .color_text = if (is_prefab_part) prefab_tint else null,
        });
    }

    {
        const cxt = gui.context(@src(), .{
            .rect = branch.button.data().borderRectScale().r,
        }, .{ .id_extra = idx });
        defer cxt.deinit();

        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = idx });
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "Rename", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                EditorState.startRenameObject(idx);
            }

            if (gui.menuItemLabel(@src(), "Duplicate", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                if (EditorState.selectedCount() > 1 and EditorState.isObjectSelected(idx)) {
                    EditorState.duplicateSelectedObjects(gui.frameTimeNS(), gui.io);
                } else {
                    EditorState.duplicateObject(gui.frameTimeNS(), gui.io, idx);
                }
            }

            if (gui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                if (EditorState.selectedCount() > 1 and EditorState.isObjectSelected(idx)) {
                    g_show_delete_dialog = true;
                } else {
                    EditorState.selected_object = idx;
                    EditorState.clearSelectedObjects();
                    EditorState.selectObject(idx);
                    g_show_delete_dialog = true;
                }
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

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
    }

    if (has_children) {
        gui.icon(
            @src(),
            "arrow",
            if (branch.expanded) gui.entypo.triangle_down else gui.entypo.triangle_right,
            .{},
            .{ .gravity_y = 0.5, .gravity_x = 1.0, .min_size_content = .{ .w = 12, .h = 12 }, .id_extra = idx },
        );

        if (branch.expander(@src(), .{ .indent = 16.0 }, .{ .expand = .horizontal, .id_extra = idx })) {
            renderLevel(tree, @intCast(idx), depth + 1, had_removed);
        }
    }
}
