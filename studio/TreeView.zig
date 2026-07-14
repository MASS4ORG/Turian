//! Shared tree-view machinery (C1): one implementation of row rendering,
//! click selection, inline rename, keyboard navigation, right-click context
//! menus and drag-reparenting with before/into/after drop zones — used by
//! both the scene hierarchy (`SceneTree.zig` over `EditorState`) and the UI
//! document hierarchy (`UiDocumentEditor.zig` over its loaded `.uidoc`).
//!
//! `TreeView(Model)` is comptime duck-typed. A model provides:
//!
//!   count() usize                          — number of rows (flat storage)
//!   parentOf(i: usize) i32                 — parent index, -1 = root
//!   name(i: usize) []const u8              — display name
//!   isSelected(i: usize) bool              — row is in the selection set
//!   isPrimary(i: usize) bool               — row is the primary selection
//!   primarySelection() ?usize              — for F2 / Delete keyboard paths
//!   select(i: usize, mods: Mods) void      — click selection (mods for multi)
//!   applyRename(i: usize, name: []const u8) void
//!   reparent(drag: usize, target: usize, zone: DropZone) void
//!   removeRequested() void                 — Delete key / context "Delete"
//!   rowIcon(i: usize, has_children: bool) RowIcon
//!
//! Optional (checked with @hasDecl):
//!   activate(i: usize) void                — double-click
//!   onDragStart(i: usize) void             — e.g. cross-panel drag payloads
//!   contextItems(i: usize, fw: *gui.FloatingMenuWidget) void
//!                                          — extra menu items below the
//!                                            shared Rename/Delete pair

const std = @import("std");
const gui = @import("gui");

/// Where a dragged node lands relative to the hovered target row.
pub const DropZone = enum { before, into, after };

/// Click modifier state passed to `Model.select`.
pub const Mods = struct {
    ctrl: bool = false,
    shift: bool = false,
};

pub const RowIcon = struct {
    /// Entypo icon glyph, drawn when `image` is null.
    bytes: []const u8 = "",
    tint: ?gui.Color = null,
    /// Real thumbnail (e.g. `PreviewSystem.imageSourceFor`) to draw instead of
    /// `bytes` when available — used by the asset tree to show the same
    /// previews the grid tiles do, just row-sized instead of tile-sized.
    image: ?gui.ImageSource = null,
};

pub fn TreeView(comptime Model: type) type {
    return struct {
        /// Index being dragged this frame, if any — exposed so panel chrome
        /// can suppress interactions that would fight the drag.
        pub var dragging_idx: ?usize = null;
        var drop_target: ?usize = null;
        var drop_zone: DropZone = .into;

        var last_click_idx: ?usize = null;
        var last_click_ns: i128 = 0;

        var rename_idx: ?usize = null;
        var rename_just_started: bool = false;
        var rename_buf: [256]u8 = undefined;

        pub fn isRenaming() bool {
            return rename_idx != null;
        }

        pub fn startRename(idx: usize) void {
            if (idx >= Model.count()) return;
            const n = Model.name(idx);
            const len = @min(n.len, rename_buf.len - 1);
            @memcpy(rename_buf[0..len], n[0..len]);
            @memset(rename_buf[len..], 0);
            rename_idx = idx;
            rename_just_started = true;
        }

        pub fn cancelRename() void {
            rename_idx = null;
            rename_just_started = false;
        }

        /// True when `maybe_ancestor` is `idx` itself or on `idx`'s parent
        /// chain — used to forbid dropping a node into its own subtree.
        pub fn isAncestorOrSelf(idx: usize, maybe_ancestor: usize) bool {
            var cur: i32 = @intCast(idx);
            var steps: usize = 0;
            const n = Model.count();
            while (cur >= 0 and steps <= n) : (steps += 1) {
                if (@as(usize, @intCast(cur)) == maybe_ancestor) return true;
                cur = Model.parentOf(@intCast(cur));
            }
            return false;
        }

        /// Draw the tree rows. Call inside the panel's scroll area; `root_wd`
        /// is the panel widget keyboard events are attributed to.
        pub fn draw(root_wd: *gui.WidgetData) void {
            if (Model.count() == 0) {
                if (isRenaming()) cancelRename();
                return;
            }

            handleKeyboard(root_wd);

            var tree = gui.TreeWidget.tree(@src(), .{ .enable_reordering = true }, .{ .expand = .horizontal });
            defer tree.deinit();

            var had_removed: bool = false;

            // Recomputed each frame while a node is dragged (see renderNode).
            drop_target = null;
            renderLevel(tree, -1, 0, &had_removed);

            // On drop, hand the drag/target/zone triple to the model — drop
            // semantics (sibling insert position, index remapping) are the
            // model's business.
            if (had_removed) {
                if (dragging_idx) |di| {
                    if (drop_target) |tgt| Model.reparent(di, tgt, drop_zone);
                }
                dragging_idx = null;
                drop_target = null;
            }
        }

        fn handleKeyboard(root_wd: *gui.WidgetData) void {
            for (gui.events()) |*e| {
                if (e.handled) continue;
                if (e.evt != .key) continue;
                const ke = e.evt.key;
                if (ke.action != .down and ke.action != .repeat) continue;

                if (isRenaming()) {
                    if (ke.code == .escape) {
                        e.handle(@src(), root_wd);
                        cancelRename();
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
                    if (Model.primarySelection()) |sel| {
                        e.handle(@src(), root_wd);
                        startRename(sel);
                        return;
                    }
                }

                if (ke.code == .delete or ke.code == .backspace) {
                    if (Model.primarySelection() != null) {
                        e.handle(@src(), root_wd);
                        Model.removeRequested();
                        return;
                    }
                }
            }
        }

        fn buildVisualOrder(out: []usize, count: *usize, parent: i32) void {
            const n = Model.count();
            var i: usize = 0;
            while (i < n) : (i += 1) {
                if (Model.parentOf(i) == parent) {
                    if (count.* < out.len) {
                        out[count.*] = i;
                        count.* += 1;
                    }
                    buildVisualOrder(out, count, @intCast(i));
                }
            }
        }

        fn navigateSelection(go_up: bool) void {
            const n = Model.count();
            const visual = gui.currentWindow().arena().alloc(usize, n) catch return;
            var count: usize = 0;
            buildVisualOrder(visual, &count, -1);
            if (count == 0) return;

            const new_idx = blk: {
                if (Model.primarySelection()) |sel| {
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

            Model.select(new_idx, .{});
        }

        /// Highlight the hovered drop target: a line at the row's top/bottom
        /// edge for a sibling drop, a translucent fill for a child drop.
        fn drawDropIndicator(row: gui.Rect.Physical, zone: DropZone) void {
            const accent = gui.currentWindow().theme.color(.highlight, .fill);
            const line = gui.Color{ .r = accent.r, .g = accent.g, .b = accent.b, .a = 255 };
            const fill = gui.Color{ .r = accent.r, .g = accent.g, .b = accent.b, .a = 70 };
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

        fn renderLevel(tree: *gui.TreeWidget, parent: i32, depth: usize, had_removed: *bool) void {
            const n = Model.count();
            var i: usize = 0;
            while (i < n) : (i += 1) {
                if (Model.parentOf(i) == parent) {
                    renderNode(tree, i, depth, had_removed);
                }
            }
        }

        fn renderNode(tree: *gui.TreeWidget, idx: usize, depth: usize, had_removed: *bool) void {
            const n = Model.count();
            const has_children = blk: {
                var c: usize = 0;
                while (c < n) : (c += 1) {
                    if (Model.parentOf(c) == @as(i32, @intCast(idx))) break :blk true;
                }
                break :blk false;
            };

            const is_primary = Model.isPrimary(idx);
            const is_selected = Model.isSelected(idx);
            const is_renaming_this = rename_idx != null and rename_idx.? == idx;

            const branch = tree.branch(@src(), .{ .expanded = depth == 0 }, .{
                .id_extra = idx,
                .expand = .horizontal,
                .background = is_primary or is_selected,
                .style = if (is_primary) .highlight else .window,
            });
            defer branch.deinit();

            if (branch.floating()) {
                dragging_idx = idx;
                if (comptime @hasDecl(Model, "onDragStart")) Model.onDragStart(idx);
            }
            if (branch.removed()) had_removed.* = true;

            // While a node is dragged, treat each row as three drop zones —
            // top quarter = before (sibling), middle = into (make child),
            // bottom quarter = after (sibling). Own subtree is skipped.
            if (dragging_idx) |di| {
                if (di != idx and !isAncestorOrSelf(idx, di)) {
                    const row = branch.button.data().borderRectScale().r;
                    const mp = gui.currentWindow().mouse_pt;
                    if (row.contains(mp)) {
                        const rel = if (row.h > 0) (mp.y - row.y) / row.h else 0.5;
                        const zone: DropZone = if (rel < 0.25) .before else if (rel > 0.75) .after else .into;
                        drop_target = idx;
                        drop_zone = zone;
                        drawDropIndicator(row, zone);
                    }
                }
            }

            if (branch.button.clicked() and !is_renaming_this) {
                const now = gui.frameTimeNS();

                var mods: Mods = .{};
                for (gui.events()) |*e| {
                    if (e.evt == .mouse) {
                        const me = e.evt.mouse;
                        mods.ctrl = me.mod.control();
                        mods.shift = me.mod.shift();
                    }
                }

                const same_idx = last_click_idx == idx;
                if (comptime @hasDecl(Model, "activate")) {
                    if (same_idx and now - last_click_ns < 500 * std.time.ns_per_ms) {
                        last_click_idx = null;
                        Model.activate(idx);
                    } else {
                        Model.select(idx, mods);
                        last_click_idx = idx;
                        last_click_ns = now;
                    }
                } else {
                    Model.select(idx, mods);
                    last_click_idx = idx;
                    last_click_ns = now;
                }
            }

            const icon = Model.rowIcon(idx, has_children);
            if (icon.image) |source| {
                _ = gui.image(@src(), .{ .source = source, .shrink = .ratio }, .{
                    .gravity_y = 0.5,
                    .min_size_content = .{ .w = 16, .h = 16 },
                    .id_extra = idx,
                });
            } else {
                gui.icon(@src(), "icon", icon.bytes, .{}, .{
                    .gravity_y = 0.5,
                    .min_size_content = .{ .w = 16, .h = 16 },
                    .id_extra = idx,
                    .color_text = icon.tint,
                });
            }

            if (is_renaming_this) {
                var te = gui.textEntry(@src(), .{
                    .text = .{ .buffer = rename_buf[0..] },
                    .placeholder = "Name",
                }, .{
                    .expand = .horizontal,
                    .gravity_y = 0.5,
                    .id_extra = idx,
                    .min_size_content = .{ .w = 100, .h = 22 },
                });
                defer te.deinit();

                // Grab keyboard focus on the first frame so the field is
                // editable without an extra click.
                if (rename_just_started) {
                    gui.focusWidget(te.data().id, null, null);
                    rename_just_started = false;
                }

                if (te.enter_pressed) {
                    Model.applyRename(idx, te.textGet());
                    cancelRename();
                }
            } else {
                gui.label(@src(), "{s}", .{Model.name(idx)}, .{
                    .gravity_y = 0.5,
                    .id_extra = idx,
                    .color_text = icon.tint,
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
                        startRename(idx);
                    }

                    if (gui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
                        fw.close();
                        // Deleting a row that's part of the current selection
                        // acts on the whole selection; otherwise select it
                        // first so the model's remove path targets it.
                        if (!Model.isSelected(idx) and !Model.isPrimary(idx)) Model.select(idx, .{});
                        Model.removeRequested();
                    }

                    if (comptime @hasDecl(Model, "contextItems")) {
                        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = idx });
                        Model.contextItems(idx, fw);
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
    };
}
