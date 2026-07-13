//! Tile-grid listing of the current folder — the asset browser's original
//! view (issue #25's preview tiles, compound-model expansion, drag/drop),
//! shared by `.grid` mode (fills the whole panel) and `.grid_tree` mode (the
//! right pane of the Grid+Tree split). See `AssetBrowser.zig` for the
//! panel-level orchestration that picks which view(s) to draw. Tile sizing
//! and label truncation live in `AssetTileLayout.zig`; compound-model
//! sub-asset tiles live in `AssetSubAssetTiles.zig` — both split out to keep
//! this file under the project's long-file budget.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const AssetActions = @import("AssetActions.zig");
const PreviewSystem = @import("preview/PreviewSystem.zig");
const AssetNav = @import("AssetNav.zig");
const AssetContextMenus = @import("AssetContextMenus.zig");
const AssetTileLayout = @import("AssetTileLayout.zig");
const AssetSubAssetTiles = @import("AssetSubAssetTiles.zig");
const AssetBrowser = @import("AssetBrowser.zig");

// Drag-hover tracking: which tile index the cursor is over during an asset drag
var drag_hover_idx: ?usize = null;

// Directory listing, collected and sorted each frame (folders first, then files,
// each alphabetical) before tiles are rendered.
const MAX_ENTRIES = 2048;
var ent_name: [MAX_ENTRIES][256]u8 = undefined;
var ent_name_len: [MAX_ENTRIES]u16 = undefined;
var ent_is_dir: [MAX_ENTRIES]bool = undefined;
var ent_order: [MAX_ENTRIES]usize = undefined;

/// Sort order: directories before files, then case-insensitive alphabetical.
fn entryLessThan(_: void, ia: usize, ib: usize) bool {
    if (ent_is_dir[ia] != ent_is_dir[ib]) return ent_is_dir[ia];
    const an = ent_name[ia][0..ent_name_len[ia]];
    const bn = ent_name[ib][0..ent_name_len[ib]];
    return std.ascii.orderIgnoreCase(an, bn) == .lt;
}

/// Render a collapsible, read-only "Packages" section listing every installed
/// package (name + version + types) and the asset directories it contributes
/// (issue #59). Package assets are authored in the package, so they are shown
/// as read-only labels rather than editable tiles.
fn drawPackagesSection(proj_path: []const u8) void {
    const arena = gui.currentWindow().arena();
    const store_root = editor.package_store.resolveRoot(arena, EditorState.environ_map) catch "";
    var pm = editor.PackageManager.discover(gui.io, arena, proj_path, editor.PackageManager.parseEngineVersion(""), store_root);
    defer pm.deinit();
    if (pm.packageCount() == 0) return;

    var label_buf: [128]u8 = undefined;
    const header = std.fmt.bufPrint(&label_buf, "Packages ({d})", .{pm.packageCount()}) catch "Packages";
    if (gui.expander(@src(), header, .{ .default_expanded = false }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } })) {
        // Labels are drawn directly into the (vertical) scroll area, mirroring
        // the ProfilerPanel pattern — one row per package, then its asset dirs.
        for (pm.packages.items, 0..) |*pkg, i| {
            const m = &pkg.manifest;
            gui.label(@src(), "  {s}  v{s}  (read-only)", .{ m.name, m.version }, .{ .id_extra = i, .expand = .horizontal });
            for (m.asset_dirs, 0..) |adir, j| {
                gui.label(@src(), "      Packages/{s}/{s}", .{ m.name, adir }, .{ .id_extra = i * 64 + j, .expand = .horizontal });
            }
        }
    }
    _ = gui.separator(@src(), .{ .expand = .horizontal });
}

/// Grid tile listing for the current folder. Shared by `.grid` (fills the
/// panel) and `.grid_tree` (right pane of the split, `show_up_button` false
/// since the folder-tree sidebar makes `..` redundant). Also draws the
/// breadcrumb (rather than `AssetBrowser`'s own header): it has little
/// value once a folder tree is on screen for navigation, so it only shows
/// up alongside the grid, never in tree-only mode.
pub fn draw(proj_path: []const u8, browse_path: []const u8, outer_wd: *gui.WidgetData, show_up_button: bool) void {
    AssetTileLayout.syncFromSettings();

    {
        var toolbar = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer toolbar.deinit();
        if (EditorState.project_path != null) AssetNav.drawBreadcrumb();
    }

    var scroll = gui.scrollArea(@src(), .{ .vertical = .auto }, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
    defer scroll.deinit();

    // Read-only "Packages" section: assets contributed by installed packages
    // (issue #59). Shown only at the assets root, and only when enabled in
    // the panel's settings menu (hidden by default). Editing happens in the
    // package, not the consuming project, so these are listed, never tiles.
    if (AssetNav.current_subdir_len == 0 and AssetBrowser.showPackages()) drawPackagesSection(proj_path);

    var dir = std.Io.Dir.cwd().openDir(gui.io, browse_path, .{ .iterate = true }) catch {
        gui.label(@src(), "No assets folder found. Create {s}/assets.", .{proj_path}, .{ .padding = .all(8) });
        return;
    };
    defer dir.close(gui.io);

    // Not `defer`red: closed explicitly right after the loop below. Expanded
    // models' sub-asset tiles are drawn inline in this same flexbox (see
    // `AssetSubAssetTiles.drawInlineSubAssets`), so they must flow before it
    // is deinit'd.
    var flex = gui.flexbox(@src(), .{}, .{ .expand = .horizontal, .padding = .all(4) });

    drag_hover_idx = null;

    // Reset nav list; will be rebuilt during tile rendering for next frame's keyboard handler
    AssetNav.beginNavList();

    var entry_idx: usize = 0;
    if (show_up_button and AssetNav.current_subdir_len > 0) {
        const up_hovered = EditorState.drag_kind == .asset and drag_hover_idx == 99999;
        var tile = gui.box(@src(), .{}, .{
            .id_extra = 99999,
            .min_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
            .max_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
            .background = true,
            .style = if (up_hovered) .highlight else .content,
            .border = .all(if (up_hovered) 2 else 1),
            .corners = .all(4),
            .margin = .all(2),
            .padding = .all(4),
            .gravity_x = 0.5,
        });
        defer tile.deinit();

        for (gui.events()) |*e| {
            if (!gui.eventMatchSimple(e, tile.data())) continue;
            switch (e.evt) {
                .mouse => |me| {
                    if (me.action == .position and EditorState.drag_kind == .asset) {
                        drag_hover_idx = 99999;
                    }
                    if (me.action == .press and me.button == .left) {
                        e.handle(@src(), tile.data());
                        const now = gui.frameTimeNS();
                        const same = std.mem.eql(u8, AssetNav.last_click_name_buf[0..AssetNav.last_click_name_len], "..");
                        if (same and now - AssetNav.last_click_ns < 500 * std.time.ns_per_ms) {
                            AssetNav.goUp();
                        }
                        @memcpy(AssetNav.last_click_name_buf[0..2], "..");
                        AssetNav.last_click_name_len = 2;
                        AssetNav.last_click_ns = now;
                    }
                    // Drop zone for dragging assets up one level
                    if (me.action == .release and me.button == .left and EditorState.drag_kind == .asset) {
                        e.handle(@src(), tile.data());
                        const src_path = EditorState.dragAssetPath();
                        if (src_path.len > 0) {
                            const dest = if (AssetNav.current_subdir_len > 0) blk: {
                                if (std.mem.lastIndexOfScalar(u8, browse_path, '/')) |sep| {
                                    break :blk browse_path[0..sep];
                                } else break :blk "";
                            } else "";
                            if (dest.len > 0) AssetActions.moveAsset(src_path, dest);
                        }
                    }
                },
                else => {},
            }
        }

        gui.icon(@src(), "up_icon", gui.entypo.arrow_up, .{}, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = 32, .h = 32 },
            .id_extra = 99999,
        });
        gui.label(@src(), "..", .{}, .{ .gravity_x = 0.5, .id_extra = 99999 });
    }

    // Collect entries (skipping .meta sidecars), then sort folders-first /
    // alphabetical so the listing order is stable and predictable.
    var ent_count: usize = 0;
    {
        var iter = dir.iterate();
        while (iter.next(gui.io) catch null) |e| {
            if (e.kind != .directory and std.mem.endsWith(u8, e.name, ".meta")) continue;
            if (ent_count >= MAX_ENTRIES) break;
            const n = @min(e.name.len, ent_name[ent_count].len);
            @memcpy(ent_name[ent_count][0..n], e.name[0..n]);
            ent_name_len[ent_count] = @intCast(n);
            ent_is_dir[ent_count] = e.kind == .directory;
            ent_order[ent_count] = ent_count;
            ent_count += 1;
        }
    }
    std.mem.sort(usize, ent_order[0..ent_count], {}, entryLessThan);

    for (ent_order[0..ent_count]) |ei| {
        const entry = .{ .name = ent_name[ei][0..ent_name_len[ei]], .is_dir = ent_is_dir[ei] };
        defer entry_idx += 1;

        const is_dir = entry.is_dir;

        // Collect into nav list for next frame's keyboard handler
        AssetNav.recordNavEntry(entry.name);
        var full_path_buf: [1024]u8 = undefined;
        const asset_type: editor.AssetType = if (is_dir) .unknown else blk: {
            const full_path = std.fmt.bufPrint(&full_path_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
            if (EditorState.assetDbReady()) {
                if (EditorState.asset_db.findByPath(full_path)) |info| break :blk info.asset_type;
            }
            break :blk editor.asset_registry.lookupByFilename(entry.name);
        };
        const desc = editor.asset_registry.get(asset_type);
        const icon_bytes = if (is_dir) gui.entypo.folder else AssetContextMenus.iconForHint(desc.icon_hint);

        const is_selected = if (EditorState.selected_asset_path) |sel_path| blk: {
            var asset_path_check: [1024]u8 = undefined;
            const ap = AssetNav.fullPathFor(entry.name, browse_path, &asset_path_check);
            break :blk std.mem.eql(u8, sel_path, ap);
        } else false;

        const is_drag_target = is_dir and EditorState.drag_kind == .asset and drag_hover_idx == entry_idx;
        var tile = gui.box(@src(), .{}, .{
            .id_extra = entry_idx,
            .min_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
            .max_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
            .background = true,
            .style = if (is_selected or is_drag_target) .highlight else .content,
            .border = .all(if (is_selected or is_drag_target) 2 else 1),
            .corners = .all(4),
            .margin = .all(2),
            .padding = .all(4),
            .gravity_x = 0.5,
        });
        // Closed explicitly at the end of the iteration (not `defer`red) so an
        // expanded model's sub-asset tiles can flow *after* this tile inside the
        // same grid flexbox — see the `AssetSubAssetTiles.drawInlineSubAssets`
        // call below. The outer entry loop has no `continue`/`return` that
        // would skip it.

        // Context menu for all tiles (including dirs)
        {
            const cxt = gui.context(@src(), .{
                .rect = tile.data().borderRectScale().r,
            }, .{ .id_extra = entry_idx });
            defer cxt.deinit();

            if (cxt.activePoint()) |cp| {
                var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = entry_idx });
                defer fw.deinit();

                AssetContextMenus.drawAssetExtraMenuItems(fw, proj_path, browse_path, entry.name, is_dir, asset_type, entry_idx);

                // Rename option for files and directories
                if (gui.menuItemLabel(@src(), "Rename", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    var asset_path: [1024]u8 = undefined;
                    const ap = AssetNav.fullPathFor(entry.name, browse_path, &asset_path);
                    EditorState.startRenameAsset(ap);
                }

                // Delete option for files and directories
                if (gui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    var asset_path: [1024]u8 = undefined;
                    AssetNav.requestDelete(AssetNav.fullPathFor(entry.name, browse_path, &asset_path));
                }

                if (is_dir) {
                    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = entry_idx });
                    var folder_path_buf: [1024]u8 = undefined;
                    const folder_path = std.fmt.bufPrint(&folder_path_buf, "{s}/{s}", .{ browse_path, entry.name }) catch browse_path;
                    AssetContextMenus.drawCreateAssetMenuItems(folder_path, 2000 + entry_idx * 2000);
                }
            }
        }

        var asset_path_buf: [1024]u8 = undefined;
        const asset_full_path: []const u8 = if (!is_dir)
            std.fmt.bufPrint(&asset_path_buf, "{s}/{s}", .{ browse_path, entry.name }) catch ""
        else
            "";

        // Handle inline rename for this entry
        const is_renaming_this = EditorState.isRenaming() and
            EditorState.g_rename.target == .asset and
            std.mem.eql(u8, EditorState.g_rename.asset_path_buf[0..EditorState.g_rename.asset_path_len], AssetNav.fullPathFor(entry.name, browse_path, &full_path_buf));

        // While renaming this entry, let mouse events reach the inline text
        // field (to position the cursor) instead of re-selecting / dragging.
        if (!is_renaming_this) {
            for (gui.events()) |*e| {
                if (!gui.eventMatchSimple(e, tile.data())) continue;
                switch (e.evt) {
                    .mouse => |me| {
                        if (me.action == .press and me.button == .left) {
                            e.handle(@src(), tile.data());
                            const now = gui.frameTimeNS();
                            const same_name = std.mem.eql(u8, AssetNav.last_click_name_buf[0..AssetNav.last_click_name_len], entry.name);
                            if (same_name and now - AssetNav.last_click_ns < 500 * std.time.ns_per_ms) {
                                if (is_dir) {
                                    AssetNav.enterSubdir(entry.name);
                                } else if (desc.open_mode != .none) {
                                    AssetContextMenus.openAsset(browse_path, entry.name, desc.open_mode);
                                }
                            } else if (is_dir) {
                                // Single-click on folder: select it so F2 / arrow keys work.
                                var folder_sel_buf: [1024]u8 = undefined;
                                const folder_sel = AssetNav.fullPathFor(entry.name, browse_path, &folder_sel_buf);
                                EditorState.selectAsset(folder_sel);
                            }
                            const n = @min(entry.name.len, AssetNav.last_click_name_buf.len);
                            @memcpy(AssetNav.last_click_name_buf[0..n], entry.name[0..n]);
                            AssetNav.last_click_name_len = n;
                            AssetNav.last_click_ns = now;
                            if (!is_dir) EditorState.startDragAsset(asset_full_path);
                        } else if (me.action == .release and me.button == .left) {
                            if (!is_dir) {
                                e.handle(@src(), tile.data());
                                EditorState.selectAsset(asset_full_path);
                            }
                        }
                    },
                    else => {},
                }
            }
        }

        const preview_source = if (is_dir) null else PreviewSystem.imageSourceFor(asset_full_path);
        if (preview_source) |source| {
            _ = gui.image(@src(), .{ .source = source, .shrink = .ratio }, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = AssetTileLayout.tile_content, .h = AssetTileLayout.tile_content },
                .id_extra = entry_idx,
            });
        } else {
            gui.icon(@src(), "tile_icon", icon_bytes, .{}, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = AssetTileLayout.tile_content, .h = AssetTileLayout.tile_content },
                .id_extra = entry_idx,
            });
        }

        // Inline rename or label
        if (is_renaming_this) {
            var te = gui.textEntry(@src(), .{
                .text = .{ .buffer = EditorState.g_rename.buf[0..] },
                .placeholder = "Filename",
            }, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = 64, .h = 22 },
                .id_extra = entry_idx,
            });
            defer te.deinit();

            // Grab keyboard focus on the first frame so the field is editable
            // (and doesn't lose focus and vanish on the next frame).
            if (EditorState.g_rename.just_started) {
                gui.focusWidget(te.data().id, null, null);
                EditorState.g_rename.just_started = false;
            }

            if (te.enter_pressed) {
                const text = te.textGet();
                EditorState.g_rename.len = text.len;
                EditorState.commitRename(gui.frameTimeNS(), gui.io);
            }
            // Escape is handled by the main keyboard handler
            // (Escape during rename cancels it)
        } else {
            var name_buf: [300]u8 = undefined;
            const display_name = AssetTileLayout.truncatedDisplayName(entry.name, is_dir, AssetTileLayout.tileContentWidth(), &name_buf);
            gui.label(@src(), "{s}", .{display_name orelse entry.name}, .{
                .gravity_x = 0.5,
                .id_extra = entry_idx,
            });
            if (display_name != null) {
                gui.tooltip(@src(), .{ .active_rect = tile.data().rectScale().r }, "{s}", .{entry.name}, .{ .id_extra = entry_idx });
            }
        }

        if (is_dir) {
            const drag_compatible = EditorState.drag_kind == .asset;
            if (drag_compatible) {
                for (gui.events()) |*e| {
                    if (!gui.eventMatchSimple(e, tile.data())) continue;
                    switch (e.evt) {
                        .mouse => |me| {
                            if (me.action == .position) {
                                drag_hover_idx = entry_idx;
                            }
                            if (me.action == .release and me.button == .left) {
                                e.handle(@src(), tile.data());
                                const src_path = EditorState.dragAssetPath();
                                if (src_path.len > 0) {
                                    var dest_buf: [1024]u8 = undefined;
                                    const dest_path = std.fmt.bufPrint(&dest_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                                    if (dest_path.len > 0) AssetActions.moveAsset(src_path, dest_path);
                                }
                            }
                        },
                        else => {},
                    }
                }
            }
        }

        tile.deinit();

        // Compound-model expansion (Unity-style): a small round toggle at the
        // model's right edge, and — when expanded — its generated materials/
        // textures enclosed in a bordered group box flowing right after it, so
        // it's visually clear those assets are embedded in the model rather than
        // real files. Drawn as siblings of the tile (their own rects), so the
        // toggle never competes with tile selection for a click.
        if (!is_dir and asset_type == .model and !is_renaming_this) {
            AssetSubAssetTiles.drawExpandToggle(asset_full_path, entry_idx);
            if (AssetSubAssetTiles.isExpanded(asset_full_path))
                AssetSubAssetTiles.drawInlineSubAssets(proj_path, asset_full_path, entry_idx);
        }
    }

    flex.deinit();

    // Commit nav list for next frame's arrow-key handler
    AssetNav.commitNavList();

    // Drop a dragged scene object onto the browser to save it (and its
    // children) as a prefab asset in the current folder.
    if (EditorState.drag_kind == .game_object) {
        for (gui.events()) |*e| {
            if (!gui.eventMatchSimple(e, outer_wd)) continue;
            if (e.evt == .mouse) {
                const me = e.evt.mouse;
                if (me.action == .release and me.button == .left) {
                    e.handle(@src(), outer_wd);
                    _ = EditorState.createPrefabFromObject(gui.frameTimeNS(), gui.io, EditorState.drag_object_idx);
                    EditorState.clearDrag();
                    gui.refresh(null, @src(), null);
                }
            }
        }
    }

    // Empty area context menu
    {
        const cxt = gui.context(@src(), .{ .rect = outer_wd.borderRectScale().r }, .{});
        defer cxt.deinit();

        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "Reveal in file manager", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                AssetActions.revealInFileManager(browse_path, "");
            }
            if (gui.menuItemLabel(@src(), "Copy Absolute Path", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                var resolve_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
                const resolve_len = std.Io.Dir.realPathFile(std.Io.Dir.cwd(), gui.io, browse_path, &resolve_buf) catch 0;
                const resolved_path = if (resolve_len > 0) resolve_buf[0..resolve_len] else browse_path;
                gui.clipboardTextSet(resolved_path);
            }
            if (gui.menuItemLabel(@src(), "Copy Relative Path", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                const sub = AssetNav.currentSubdir();
                var copy_rel_buf: [1024]u8 = undefined;
                const copy_rel = if (sub.len > 0)
                    std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{sub}) catch "assets"
                else
                    "assets";
                gui.clipboardTextSet(copy_rel);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
            AssetContextMenus.drawCreateAssetMenuItems(browse_path, 0);
        }
    }
}
