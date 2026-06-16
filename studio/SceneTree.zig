const std = @import("std");
const dvui = @import("dvui");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

var g_dragging_idx: ?usize = null;

var g_last_click_idx: ?usize = null;
var g_last_click_ns: i128 = 0;

var g_show_delete_dialog: bool = false;
var g_delete_dialog_result: ?bool = null;

/// Draw the scene hierarchy tree with drag-and-drop reordering.
pub fn draw() void {
    var outer = dvui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    {
        var header = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();
        dvui.label(@src(), "Scene Hierarchy", .{}, .{ .font = .theme(.heading) });
        if (EditorState.scene_dirty) {
            dvui.label(@src(), " *", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });
        }
    }

    var scroll = dvui.scrollArea(@src(), .{}, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
    defer scroll.deinit();

    if (EditorState.object_count == 0) {
        // Blank state is cleaner than a placeholder message.
        if (EditorState.isRenaming()) EditorState.cancelRename();
        return;
    }

    handleKeyboard(outer.data());

    handleDeleteDialog();

    var tree = dvui.TreeWidget.tree(@src(), .{ .enable_reordering = true }, .{ .expand = .horizontal });
    defer tree.deinit();

    var insert_before_idx: ?usize = null;
    var had_removed: bool = false;

    renderLevel(tree, -1, 0, &insert_before_idx, &had_removed);

    if (insert_before_idx) |ibi| {
        if (g_dragging_idx) |di| {
            if (di != ibi) {
                EditorState.moveObjectBefore(dvui.frameTimeNS(), di, ibi);
                EditorState.scene_dirty = true;
            }
            g_dragging_idx = null;
        }
    } else if (had_removed) {
        g_dragging_idx = null;
    }
}

fn handleKeyboard(root_wd: *dvui.WidgetData) void {
    for (dvui.events()) |*e| {
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
                EditorState.deleteSelectedObjects(dvui.frameTimeNS());
            }
        }
    }

    if (!g_show_delete_dialog) return;
    _ = EditorState.selected_object orelse {
        g_show_delete_dialog = false;
        return;
    };

    dvui.dialog(@src(), .{}, .{
        .title = "Delete Object",
        .message = "Delete selected object and all its children?",
        .ok_label = "Delete",
        .cancel_label = "Cancel",
        .default = .cancel,
        .callafterFn = struct {
            fn callafter(_: dvui.Id, response: dvui.enums.DialogResponse) !void {
                g_delete_dialog_result = response == .ok;
            }
        }.callafter,
    });
}

fn renderLevel(tree: *dvui.TreeWidget, parent: i32, depth: usize, insert_before: *?usize, had_removed: *bool) void {
    for (EditorState.objects[0..EditorState.object_count], 0..) |*obj, i| {
        if (obj.parent == parent) {
            renderNode(tree, i, obj, depth, insert_before, had_removed);
        }
    }
}

fn renderNode(tree: *dvui.TreeWidget, idx: usize, obj: *EditorState.SceneNode, depth: usize, insert_before: *?usize, had_removed: *bool) void {
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
    if (branch.insertBefore()) insert_before.* = idx;
    if (branch.removed()) had_removed.* = true;

    if (branch.button.clicked() and !is_renaming_this) {
        const now = dvui.frameTimeNS();

        var ctrl_held = false;
        var shift_held = false;
        for (dvui.events()) |*e| {
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

    const icon_bytes = if (has_children) dvui.entypo.folder else dvui.entypo.text_document;
    dvui.icon(@src(), "icon", icon_bytes, .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 16, .h = 16 },
        .id_extra = idx,
    });

    if (is_renaming_this) {
        var te = dvui.textEntry(@src(), .{
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
            dvui.focusWidget(te.data().id, null, null);
            EditorState.g_rename.just_started = false;
        }

        if (te.enter_pressed) {
            const text = te.textGet();
            EditorState.g_rename.len = text.len;
            EditorState.commitRename(dvui.frameTimeNS(), dvui.io);
        }
    } else {
        dvui.label(@src(), "{s}", .{obj.nameSlice()}, .{ .gravity_y = 0.5, .id_extra = idx });
    }

    {
        const cxt = dvui.context(@src(), .{
            .rect = branch.button.data().borderRectScale().r,
        }, .{ .id_extra = idx });
        defer cxt.deinit();

        if (cxt.activePoint()) |cp| {
            var fw = dvui.floatingMenu(@src(), .{ .from = dvui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = idx });
            defer fw.deinit();

            if (dvui.menuItemLabel(@src(), "Rename", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                EditorState.startRenameObject(idx);
            }

            if (dvui.menuItemLabel(@src(), "Duplicate", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                if (EditorState.selectedCount() > 1 and EditorState.isObjectSelected(idx)) {
                    EditorState.duplicateSelectedObjects(dvui.frameTimeNS(), dvui.io);
                } else {
                    EditorState.duplicateObject(dvui.frameTimeNS(), dvui.io, idx);
                }
            }

            if (dvui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
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

            _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

            if (dvui.menuItemLabel(@src(), "Copy GUID", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                const gs = EditorState.objects[idx].guidSlice();
                if (gs.len > 0) dvui.clipboardTextSet(gs);
            }

            if (dvui.menuItemLabel(@src(), "Frame Object", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                fw.close();
                EditorState.focusOnObject(idx);
            }
        }
    }

    if (has_children) {
        dvui.icon(
            @src(),
            "arrow",
            if (branch.expanded) dvui.entypo.triangle_down else dvui.entypo.triangle_right,
            .{},
            .{ .gravity_y = 0.5, .gravity_x = 1.0, .min_size_content = .{ .w = 12, .h = 12 }, .id_extra = idx },
        );

        if (branch.expander(@src(), .{ .indent = 16.0 }, .{ .expand = .horizontal, .id_extra = idx })) {
            renderLevel(tree, @intCast(idx), depth + 1, insert_before, had_removed);
        }
    }
}
