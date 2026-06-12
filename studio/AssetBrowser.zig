const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const ProjectOps = @import("ProjectOps.zig");

var last_click_name_buf: [256]u8 = undefined;
var last_click_name_len: usize = 0;
var last_click_ns: i128 = 0;

var current_subdir_buf: [512]u8 = undefined;
var current_subdir_len: usize = 0;

var prev_project_path_buf: [1024]u8 = undefined;
var prev_project_path_len: usize = 0;

// Delete confirmation state
var g_pending_delete_path: [1024]u8 = undefined;
var g_pending_delete_len: usize = 0;
var g_show_delete_dialog: bool = false;
var g_delete_dialog_result: ?bool = null;

// Drag-hover tracking: which tile index the cursor is over during an asset drag
var g_drag_hover_idx: ?usize = null;

fn currentSubdir() []const u8 {
    return current_subdir_buf[0..current_subdir_len];
}

fn enterSubdir(name: []const u8) void {
    if (current_subdir_len == 0) {
        const len = @min(name.len, current_subdir_buf.len);
        @memcpy(current_subdir_buf[0..len], name[0..len]);
        current_subdir_len = len;
    } else {
        const available = current_subdir_buf.len - current_subdir_len - 1;
        const len = @min(name.len, available);
        current_subdir_buf[current_subdir_len] = '/';
        @memcpy(current_subdir_buf[current_subdir_len + 1 .. current_subdir_len + 1 + len], name[0..len]);
        current_subdir_len += 1 + len;
    }
    last_click_name_len = 0;
}

fn goUp() void {
    const sub = currentSubdir();
    if (std.mem.lastIndexOfScalar(u8, sub, '/')) |sep| {
        current_subdir_len = sep;
    } else {
        current_subdir_len = 0;
    }
    last_click_name_len = 0;
}

fn fullPathFor(name: []const u8, browse_path: []const u8, buf: []u8) []const u8 {
    const sub = currentSubdir();
    return if (sub.len > 0)
        std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch ""
    else
        std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch "";
}

/// Draw the asset browser panel with file tiles and navigation.
pub fn draw() void {
    var outer = dvui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    const cur_path_slice = EditorState.project_path orelse "";
    const prev_path_slice = prev_project_path_buf[0..prev_project_path_len];
    if (!std.mem.eql(u8, cur_path_slice, prev_path_slice)) {
        current_subdir_len = 0;
        last_click_name_len = 0;
        const n = @min(cur_path_slice.len, prev_project_path_buf.len);
        @memcpy(prev_project_path_buf[0..n], cur_path_slice[0..n]);
        prev_project_path_len = n;
    }

    {
        var header = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();

        dvui.label(@src(), "Asset Browser", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });

        if (EditorState.project_path) |p| {
            if (current_subdir_len > 0) {
                dvui.label(@src(), "  {s}/assets/{s}", .{ p, currentSubdir() }, .{ .gravity_y = 0.5, .expand = .horizontal });
            } else {
                dvui.label(@src(), "  {s}/assets", .{p}, .{ .gravity_y = 0.5, .expand = .horizontal });
            }
        }

        if (EditorState.project_path != null) {
            if (dvui.button(@src(), "Refresh", .{}, .{ .gravity_y = 0.5 })) {
                EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            }
        }
    }

    const proj_path = EditorState.project_path orelse {
        var center = dvui.box(@src(), .{}, .{
            .expand = .both,
            .gravity_x = 0.5,
            .gravity_y = 0.5,
        });
        defer center.deinit();
        dvui.label(@src(), "No project open.", .{}, .{ .gravity_x = 0.5 });
        dvui.label(@src(), "Use File > Open Project... to open a project folder.", .{}, .{ .gravity_x = 0.5 });
        return;
    };

    var assets_path_buf: [1024]u8 = undefined;
    const assets_path = std.fmt.bufPrint(&assets_path_buf, "{s}/assets", .{proj_path}) catch {
        dvui.label(@src(), "Path too long.", .{}, .{});
        return;
    };

    var browse_path_buf: [1024]u8 = undefined;
    const browse_path: []const u8 = if (current_subdir_len > 0)
        std.fmt.bufPrint(&browse_path_buf, "{s}/{s}", .{ assets_path, currentSubdir() }) catch assets_path
    else
        assets_path;

    // Handle keyboard events (Escape cancels rename, F2 starts rename)
    for (dvui.events()) |*e| {
        if (e.handled) continue;
        if (e.evt == .key) {
            const ke = e.evt.key;
            if (ke.action != .down) continue;
            const mod = ke.mod;
            if (mod.control() and ke.code == .c and EditorState.selected_asset_path != null) {
                e.handle(@src(), outer.data());
                dvui.clipboardTextSet(EditorState.selected_asset_path.?);
            } else if (ke.code == .escape and EditorState.isRenaming()) {
                e.handle(@src(), outer.data());
                EditorState.cancelRename();
            } else if (ke.code == .f2 and !EditorState.isRenaming() and EditorState.selected_asset_path != null) {
                e.handle(@src(), outer.data());
                EditorState.startRenameAsset(EditorState.selected_asset_path.?);
            }
        }
    }

    // Handle delete confirmation dialog
    handleDeleteDialog();

    var scroll = dvui.scrollArea(@src(), .{ .vertical = .auto }, .{ .expand = .both });
    defer scroll.deinit();

    var dir = std.Io.Dir.cwd().openDir(dvui.io, browse_path, .{ .iterate = true }) catch {
        dvui.label(@src(), "No assets folder found. Create {s}/assets.", .{proj_path}, .{ .padding = .all(8) });
        return;
    };
    defer dir.close(dvui.io);

    var flex = dvui.flexbox(@src(), .{}, .{ .expand = .horizontal, .padding = .all(4) });
    defer flex.deinit();

    // Reset drag-hover each frame; set again below via move events
    g_drag_hover_idx = null;

    var entry_idx: usize = 0;
    if (current_subdir_len > 0) {
        const up_hovered = EditorState.drag_kind == .asset and g_drag_hover_idx == 99999;
        var tile = dvui.box(@src(), .{}, .{
            .id_extra = 99999,
            .min_size_content = .{ .w = 72, .h = 72 },
            .background = true,
            .style = if (up_hovered) .highlight else .content,
            .border = .all(if (up_hovered) 2 else 1),
            .corner_radius = .all(4),
            .margin = .all(2),
            .padding = .all(4),
            .gravity_x = 0.5,
        });
        defer tile.deinit();

        for (dvui.events()) |*e| {
            if (!dvui.eventMatchSimple(e, tile.data())) continue;
            switch (e.evt) {
                .mouse => |me| {
                    if (me.action == .position and EditorState.drag_kind == .asset) {
                        g_drag_hover_idx = 99999;
                    }
                    if (me.action == .press and me.button == .left) {
                        e.handle(@src(), tile.data());
                        const now = dvui.frameTimeNS();
                        const same = std.mem.eql(u8, last_click_name_buf[0..last_click_name_len], "..");
                        if (same and now - last_click_ns < 500 * std.time.ns_per_ms) {
                            goUp();
                        }
                        @memcpy(last_click_name_buf[0..2], "..");
                        last_click_name_len = 2;
                        last_click_ns = now;
                    }
                    // Drop zone for dragging assets up one level
                    if (me.action == .release and me.button == .left and EditorState.drag_kind == .asset) {
                        e.handle(@src(), tile.data());
                        const src_path = EditorState.dragAssetPath();
                        if (src_path.len > 0) {
                            const dest = if (current_subdir_len > 0) blk: {
                                if (std.mem.lastIndexOfScalar(u8, browse_path, '/')) |sep| {
                                    break :blk browse_path[0..sep];
                                } else break :blk "";
                            } else "";
                            if (dest.len > 0) {
                                if (std.mem.lastIndexOfScalar(u8, src_path, '/')) |sep| {
                                    const file_name = src_path[sep + 1 ..];
                                    var full_dest_buf: [1024]u8 = undefined;
                                    const full_dest = std.fmt.bufPrint(&full_dest_buf, "{s}/{s}", .{ dest, file_name }) catch "";
                                    if (full_dest.len > 0) {
                                        std.Io.Dir.rename(std.Io.Dir.cwd(), src_path, std.Io.Dir.cwd(), full_dest, dvui.io) catch {};
                                        var src_meta_buf: [1024 + 5]u8 = undefined;
                                        var dest_meta_buf: [1024 + 5]u8 = undefined;
                                        const src_meta = std.fmt.bufPrint(&src_meta_buf, "{s}.meta", .{src_path}) catch "";
                                        const dest_meta = std.fmt.bufPrint(&dest_meta_buf, "{s}.meta", .{full_dest}) catch "";
                                        if (src_meta.len > 0 and dest_meta.len > 0) {
                                            std.Io.Dir.rename(std.Io.Dir.cwd(), src_meta, std.Io.Dir.cwd(), dest_meta, dvui.io) catch {};
                                        }
                                        EditorState.clearDrag();
                                        EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
                                    }
                                }
                            }
                        }
                    }
                },
                else => {},
            }
        }

        dvui.icon(@src(), "up_icon", dvui.entypo.arrow_up, .{}, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = 32, .h = 32 },
            .id_extra = 99999,
        });
        dvui.label(@src(), "..", .{}, .{ .gravity_x = 0.5, .id_extra = 99999 });
    }

    var iter = dir.iterate();
    while (iter.next(dvui.io) catch null) |entry| {
        if (entry.kind != .directory and std.mem.endsWith(u8, entry.name, ".meta")) continue;
        defer entry_idx += 1;

        const is_dir = entry.kind == .directory;
        var full_path_buf: [1024]u8 = undefined;
        const asset_type: editor.AssetType = if (is_dir) .unknown else blk: {
            const full_path = std.fmt.bufPrint(&full_path_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
            if (EditorState.assetDbReady()) {
                if (EditorState.asset_db.findByPath(full_path)) |info| break :blk info.asset_type;
            }
            break :blk editor.asset_registry.lookupByFilename(entry.name);
        };
        const desc = editor.asset_registry.get(asset_type);
        const icon_bytes = if (is_dir)
            dvui.entypo.folder
        else switch (desc.icon_hint) {
            .document => dvui.entypo.text_document,
            .code => dvui.entypo.code,
            .image => dvui.entypo.image,
            .sound => dvui.entypo.sound,
            .model => dvui.entypo.layers,
            .material => dvui.entypo.colours,
            .data => dvui.entypo.database,
        };

        // Check if this asset is the selected one
        const is_selected = if (EditorState.selected_asset_path) |sel_path| blk: {
            var asset_path_check: [1024]u8 = undefined;
            const ap = fullPathFor(entry.name, browse_path, &asset_path_check);
            break :blk std.mem.eql(u8, sel_path, ap);
        } else false;

        const is_drag_target = is_dir and EditorState.drag_kind == .asset and g_drag_hover_idx == entry_idx;
        var tile = dvui.box(@src(), .{}, .{
            .id_extra = entry_idx,
            .min_size_content = .{ .w = 72, .h = 72 },
            .background = true,
            .style = if (is_selected or is_drag_target) .highlight else .content,
            .border = .all(if (is_selected or is_drag_target) 2 else 1),
            .corner_radius = .all(4),
            .margin = .all(2),
            .padding = .all(4),
            .gravity_x = 0.5,
        });
        defer tile.deinit();

        // Context menu for all tiles (including dirs)
        {
            const cxt = dvui.context(@src(), .{
                .rect = tile.data().borderRectScale().r,
            }, .{ .id_extra = entry_idx });
            defer cxt.deinit();

            if (cxt.activePoint()) |cp| {
                var fw = dvui.floatingMenu(@src(), .{ .from = dvui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = entry_idx });
                defer fw.deinit();

                if (!is_dir and desc.open_mode != .none) {
                    const open_label = switch (desc.open_mode) {
                        .internal_editor => "Open",
                        .external_editor => "Open in External Editor",
                        .none => unreachable,
                    };
                    if (dvui.menuItemLabel(@src(), open_label, .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        openAsset(proj_path, browse_path, entry.name, desc.open_mode);
                    }
                }

                // Rename option for files and directories
                if (dvui.menuItemLabel(@src(), "Rename", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    var asset_path: [1024]u8 = undefined;
                    const ap = fullPathFor(entry.name, browse_path, &asset_path);
                    EditorState.startRenameAsset(ap);
                }

                // Delete option for files and directories
                if (dvui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    var asset_path: [1024]u8 = undefined;
                    const ap = fullPathFor(entry.name, browse_path, &asset_path);
                    const n = @min(ap.len, g_pending_delete_path.len);
                    @memcpy(g_pending_delete_path[0..n], ap[0..n]);
                    g_pending_delete_len = n;
                    g_show_delete_dialog = true;
                }

                if (!is_dir) {
                    var reimport_path_buf: [1024]u8 = undefined;
                    const reimport_path = std.fmt.bufPrint(&reimport_path_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                    if (dvui.menuItemLabel(@src(), "Reimport Asset", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        editor.asset_importer.importAssetForce(dvui.io, dvui.currentWindow().arena(), proj_path, reimport_path);
                    }
                }

                if (dvui.menuItemLabel(@src(), "Reveal in file manager", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    revealInFileManager(browse_path, entry.name);
                }

                if (!is_dir) {
                    if (dvui.menuItemLabel(@src(), "Copy Absolute Path", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        var copy_abs_raw_buf: [1024]u8 = undefined;
                        const copy_abs_raw = std.fmt.bufPrint(&copy_abs_raw_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                        var copy_abs_resolved_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
                        const copy_abs_len = std.Io.Dir.realPathFile(std.Io.Dir.cwd(), dvui.io, copy_abs_raw, &copy_abs_resolved_buf) catch 0;
                        const copy_abs = if (copy_abs_len > 0) copy_abs_resolved_buf[0..copy_abs_len] else copy_abs_raw;
                        dvui.clipboardTextSet(copy_abs);
                    }
                    if (dvui.menuItemLabel(@src(), "Copy Relative Path", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        const sub = currentSubdir();
                        var copy_rel_buf: [1024]u8 = undefined;
                        const copy_rel = if (sub.len > 0)
                            std.fmt.bufPrint(&copy_rel_buf, "assets/{s}/{s}", .{ sub, entry.name }) catch ""
                        else
                            std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{entry.name}) catch "";
                        dvui.clipboardTextSet(copy_rel);
                    }

                    var guid_buf: [36]u8 = undefined;
                    const maybe_guid_str = if (EditorState.assetDbReady()) blk: {
                        var gp_buf: [1024]u8 = undefined;
                        const gp = std.fmt.bufPrint(&gp_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                        if (EditorState.asset_db.findByPath(gp)) |info| {
                            break :blk info.guid.toString(&guid_buf);
                        }
                        break :blk "";
                    } else "";

                    if (maybe_guid_str.len > 0) {
                        if (dvui.menuItemLabel(@src(), "Copy GUID", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                            fw.close();
                            dvui.clipboardTextSet(maybe_guid_str);
                        }
                    }
                }
            }
        }

        var asset_path_buf: [1024]u8 = undefined;
        const asset_full_path: []const u8 = if (!is_dir) blk: {
            const sub = currentSubdir();
            break :blk if (sub.len > 0)
                std.fmt.bufPrint(&asset_path_buf, "{s}/{s}/{s}", .{ assets_path, sub, entry.name }) catch ""
            else
                std.fmt.bufPrint(&asset_path_buf, "{s}/{s}", .{ assets_path, entry.name }) catch "";
        } else "";

        // Handle inline rename for this entry
        const is_renaming_this = EditorState.isRenaming() and
            EditorState.g_rename.target == .asset and
            std.mem.eql(u8, EditorState.g_rename.asset_path_buf[0..EditorState.g_rename.asset_path_len], fullPathFor(entry.name, browse_path, &full_path_buf));

        for (dvui.events()) |*e| {
            if (!dvui.eventMatchSimple(e, tile.data())) continue;
            switch (e.evt) {
                .mouse => |me| {
                    if (me.action == .press and me.button == .left) {
                        e.handle(@src(), tile.data());
                        const now = dvui.frameTimeNS();
                        const same_name = std.mem.eql(u8, last_click_name_buf[0..last_click_name_len], entry.name);
                        if (same_name and now - last_click_ns < 500 * std.time.ns_per_ms) {
                            if (is_dir) {
                                enterSubdir(entry.name);
                            } else if (desc.open_mode != .none) {
                                openAsset(proj_path, browse_path, entry.name, desc.open_mode);
                            }
                        }
                        const n = @min(entry.name.len, last_click_name_buf.len);
                        @memcpy(last_click_name_buf[0..n], entry.name[0..n]);
                        last_click_name_len = n;
                        last_click_ns = now;
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

        dvui.icon(@src(), "tile_icon", icon_bytes, .{}, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = 32, .h = 32 },
            .id_extra = entry_idx,
        });

        // Inline rename or label
        if (is_renaming_this) {
            var te = dvui.textEntry(@src(), .{
                .text = .{ .buffer = EditorState.g_rename.buf[0..] },
                .placeholder = "Filename",
            }, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = 64, .h = 22 },
                .id_extra = entry_idx,
            });
            defer te.deinit();

            if (te.enter_pressed) {
                const text = te.textGet();
                EditorState.g_rename.len = text.len;
                EditorState.commitRename(dvui.frameTimeNS(), dvui.io);
            }
            // Escape is handled by the main keyboard handler
            // (Escape during rename cancels it)
        } else {
            dvui.label(@src(), "{s}", .{entry.name}, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = 64 },
                .id_extra = entry_idx,
            });
        }

        // Drop zone for folders: accept asset drops to move files
        if (is_dir) {
            const drag_compatible = EditorState.drag_kind == .asset;
            if (drag_compatible) {
                for (dvui.events()) |*e| {
                    if (!dvui.eventMatchSimple(e, tile.data())) continue;
                    switch (e.evt) {
                        .mouse => |me| {
                            if (me.action == .position) {
                                g_drag_hover_idx = entry_idx;
                            }
                            if (me.action == .release and me.button == .left) {
                                e.handle(@src(), tile.data());
                                // Move the dragged asset into this directory
                                const src_path = EditorState.dragAssetPath();
                                if (src_path.len > 0) {
                                    var dest_buf: [1024]u8 = undefined;
                                    const dest_path = std.fmt.bufPrint(&dest_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                                    if (dest_path.len > 0) {
                                        // Get the filename from the source path
                                        if (std.mem.lastIndexOfScalar(u8, src_path, '/')) |sep| {
                                            const file_name = src_path[sep + 1 ..];
                                            var full_dest_buf: [1024]u8 = undefined;
                                            const full_dest = std.fmt.bufPrint(&full_dest_buf, "{s}/{s}", .{ dest_path, file_name }) catch "";
                                            if (full_dest.len > 0) {
                                                std.Io.Dir.rename(std.Io.Dir.cwd(), src_path, std.Io.Dir.cwd(), full_dest, dvui.io) catch {};
                                                // Also move .meta file
                                                var src_meta_buf: [1024 + 5]u8 = undefined;
                                                var dest_meta_buf: [1024 + 5]u8 = undefined;
                                                const src_meta = std.fmt.bufPrint(&src_meta_buf, "{s}.meta", .{src_path}) catch "";
                                                const dest_meta = std.fmt.bufPrint(&dest_meta_buf, "{s}.meta", .{full_dest}) catch "";
                                                if (src_meta.len > 0 and dest_meta.len > 0) {
                                                    std.Io.Dir.rename(std.Io.Dir.cwd(), src_meta, std.Io.Dir.cwd(), dest_meta, dvui.io) catch {};
                                                }
                                                EditorState.clearDrag();
                                                EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        else => {},
                    }
                }
            }
        }
    }

    // Empty area context menu
    {
        const cxt = dvui.context(@src(), .{ .rect = outer.data().borderRectScale().r }, .{});
        defer cxt.deinit();

        if (cxt.activePoint()) |cp| {
            var fw = dvui.floatingMenu(@src(), .{ .from = dvui.Rect.Natural.fromPoint(cp) }, .{});
            defer fw.deinit();

            if (dvui.menuItemLabel(@src(), "Reveal in file manager", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                revealInFileManager(browse_path, "");
            }
            if (dvui.menuItemLabel(@src(), "Copy Absolute Path", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                var resolve_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
                const resolve_len = std.Io.Dir.realPathFile(std.Io.Dir.cwd(), dvui.io, browse_path, &resolve_buf) catch 0;
                const resolved_path = if (resolve_len > 0) resolve_buf[0..resolve_len] else browse_path;
                dvui.clipboardTextSet(resolved_path);
            }
            if (dvui.menuItemLabel(@src(), "Copy Relative Path", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                const sub = currentSubdir();
                var copy_rel_buf: [1024]u8 = undefined;
                const copy_rel = if (sub.len > 0)
                    std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{sub}) catch "assets"
                else
                    "assets";
                dvui.clipboardTextSet(copy_rel);
            }

            _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

            if (dvui.menuItemLabel(@src(), "Create New Scene", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                createNewScene(browse_path);
            }
            if (dvui.menuItemLabel(@src(), "Create New Input Actions", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                createNewInputActions(browse_path);
            }
            for (engine.Material.presets, 0..) |preset, pi| {
                var label_buf: [64]u8 = undefined;
                const label = std.fmt.bufPrint(&label_buf, "New Material: {s}", .{preset.name}) catch continue;
                if (dvui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = pi }) != null) {
                    fw.close();
                    createNewMaterialFromPreset(browse_path, preset);
                }
            }
            for (EditorState.discovered_components[0..EditorState.discovered_count], 0..) |*def, di| {
                if (def.kind != .data_asset) continue;
                var label_buf: [128]u8 = undefined;
                const label = std.fmt.bufPrint(&label_buf, "New {s}", .{def.displayName()}) catch continue;
                if (dvui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = 1000 + di }) != null) {
                    fw.close();
                    createNewDataAsset(browse_path, def);
                }
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
                const del_path = g_pending_delete_path[0..g_pending_delete_len];
                std.Io.Dir.cwd().deleteFile(dvui.io, del_path) catch {
                    std.Io.Dir.cwd().deleteDir(dvui.io, del_path) catch {};
                };
                var meta_buf: [1024 + 5]u8 = undefined;
                const meta_path = std.fmt.bufPrint(&meta_buf, "{s}.meta", .{del_path}) catch "";
                if (meta_path.len > 0) {
                    std.Io.Dir.cwd().deleteFile(dvui.io, meta_path) catch {};
                }
                if (EditorState.selected_asset_path) |sel| {
                    if (std.mem.eql(u8, sel, del_path)) {
                        EditorState.clearSelectedAsset();
                    }
                }
                EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            }
            g_pending_delete_len = 0;
        }
    }

    if (!g_show_delete_dialog) return;
    if (g_pending_delete_len == 0) return;

    const path = g_pending_delete_path[0..g_pending_delete_len];
    const file_name = if (std.mem.lastIndexOfScalar(u8, path, '/')) |sep|
        path[sep + 1 ..]
    else
        path;

    var msg_buf: [512]u8 = undefined;
    const msg = std.fmt.bufPrint(&msg_buf, "Delete '{s}' permanently?", .{file_name}) catch "Delete permanently?";
    dvui.dialog(@src(), .{}, .{
        .title = "Delete Asset",
        .message = msg,
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

fn openAsset(proj_path: []const u8, browse_path: []const u8, file_name: []const u8, open_mode: editor.asset_registry.OpenMode) void {
    switch (open_mode) {
        .internal_editor => openScene(proj_path, file_name),
        .external_editor => openExternal(browse_path, file_name),
        .none => {},
    }
}

fn openScene(proj_path: []const u8, file_name: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    const sub = currentSubdir();
    const full_path = if (sub.len > 0)
        std.fmt.bufPrint(&path_buf, "{s}/assets/{s}/{s}", .{ proj_path, sub, file_name }) catch return
    else
        std.fmt.bufPrint(&path_buf, "{s}/assets/{s}", .{ proj_path, file_name }) catch return;
    EditorState.selected_object = null;
    EditorState.clearSelectedAsset();
    _ = ProjectOps.loadScene(full_path);
}

fn openExternal(browse_path: []const u8, file_name: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
    const builtin = @import("builtin");
    if (comptime builtin.os.tag == .windows) {
        const argv = [_][]const u8{ "cmd.exe", "/c", "start", "", path };
        const child = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        _ = child;
    } else if (comptime builtin.os.tag == .macos) {
        const argv = [_][]const u8{ "open", path };
        const child = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        _ = child;
    } else {
        const argv = [_][]const u8{ "xdg-open", path };
        const child = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        _ = child;
    }
}

fn revealInFileManager(browse_path: []const u8, file_name: []const u8) void {
    const builtin = @import("builtin");
    if (comptime builtin.os.tag == .windows) {
        if (file_name.len > 0) {
            var path_buf: [1024]u8 = undefined;
            const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
            const argv = [_][]const u8{ "explorer.exe", "/select,", path };
            _ = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        } else {
            const argv = [_][]const u8{ "explorer.exe", browse_path };
            _ = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        }
    } else if (comptime builtin.os.tag == .macos) {
        if (file_name.len > 0) {
            var path_buf: [1024]u8 = undefined;
            const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
            const argv = [_][]const u8{ "open", "-R", path };
            _ = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        } else {
            const argv = [_][]const u8{ "open", browse_path };
            _ = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
        }
    } else {
        const argv = [_][]const u8{ "xdg-open", browse_path };
        _ = std.process.spawn(dvui.io, .{ .argv = &argv }) catch return;
    }
}

fn createNewScene(browse_path: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    var name_buf: [64]u8 = undefined;
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const file_name = if (n == 0)
            std.fmt.bufPrint(&name_buf, "new_scene.json", .{}) catch return
        else
            std.fmt.bufPrint(&name_buf, "new_scene_{d}.json", .{n}) catch return;

        const full_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;

        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(dvui.io, full_path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) {
            ProjectOps.saveScene(full_path);
            EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            EditorState.selectAsset(full_path);
            return;
        }
    }
}

fn createNewMaterialFromPreset(browse_path: []const u8, preset: engine.Material.Preset) void {
    var path_buf: [1024]u8 = undefined;
    var name_buf: [64]u8 = undefined;
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const file_name = if (n == 0)
            std.fmt.bufPrint(&name_buf, "new_material.material", .{}) catch return
        else
            std.fmt.bufPrint(&name_buf, "new_material_{d}.material", .{n}) catch return;

        const full_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;

        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(dvui.io, full_path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) {
            engine.Material.savePreset(preset, engine.shader.default(), dvui.io, full_path) catch return;
            EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            EditorState.selectAsset(full_path);
            return;
        }
    }
}

fn createNewInputActions(browse_path: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    var name_buf: [192]u8 = undefined;
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const file_name = if (n == 0)
            std.fmt.bufPrint(&name_buf, "input.inputactions", .{}) catch return
        else
            std.fmt.bufPrint(&name_buf, "input_{d}.inputactions", .{n}) catch return;

        const full_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;

        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(dvui.io, full_path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) {
            const default_ia = engine.InputActions{
                .version = engine.InputActions.CURRENT_VERSION,
                .actions = &.{
                    .{ .name = "jump", .kind = .button, .pos = &.{.{ .device = .key, .code = "space" }} },
                },
            };
            default_ia.save(dvui.io, full_path) catch return;
            EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            EditorState.selectAsset(full_path);
            return;
        }
    }
}

fn createNewDataAsset(browse_path: []const u8, def: *const editor.ComponentDef) void {
    var path_buf: [1024]u8 = undefined;
    var name_buf: [192]u8 = undefined;
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const type_name = def.typeName();
        const lc_name = blk: {
            var buf: [128]u8 = undefined;
            const tl = @min(type_name.len, buf.len);
            for (type_name[0..tl], 0..) |c, i| buf[i] = std.ascii.toLower(c);
            break :blk buf[0..tl];
        };
        const file_name = if (n == 0)
            std.fmt.bufPrint(&name_buf, "{s}.asset", .{lc_name}) catch return
        else
            std.fmt.bufPrint(&name_buf, "{s}_{d}.asset", .{ lc_name, n }) catch return;

        const full_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;

        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(dvui.io, full_path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) {
            const file = editor.data_asset_io.defaultFromDef(def);
            editor.data_asset_io.save(dvui.io, full_path, file) catch return;
            EditorState.refreshComponents(dvui.io, dvui.currentWindow().arena());
            EditorState.selectAsset(full_path);
            return;
        }
    }
}
