const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const AssetActions = @import("AssetActions.zig");
const Documents = @import("Documents.zig");

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

// Navigation list: entry names collected each frame for arrow-key navigation.
// Used the NEXT frame so the keyboard handler can reference the previous listing.
const NAV_MAX = 256;
var g_nav_names: [NAV_MAX][256]u8 = undefined;
var g_nav_name_lens: [NAV_MAX]usize = [_]usize{0} ** NAV_MAX;
var g_nav_is_dir: [NAV_MAX]bool = [_]bool{false} ** NAV_MAX;
var g_nav_count: usize = 0;
var g_nav_has_up: bool = false; // whether ".." tile is present

// Directory listing, collected and sorted each frame (folders first, then files,
// each alphabetical) before tiles are rendered.
const MAX_ENTRIES = 2048;
var g_ent_name: [MAX_ENTRIES][256]u8 = undefined;
var g_ent_name_len: [MAX_ENTRIES]u16 = undefined;
var g_ent_is_dir: [MAX_ENTRIES]bool = undefined;
var g_ent_order: [MAX_ENTRIES]usize = undefined;

/// Sort order: directories before files, then case-insensitive alphabetical.
fn entryLessThan(_: void, ia: usize, ib: usize) bool {
    if (g_ent_is_dir[ia] != g_ent_is_dir[ib]) return g_ent_is_dir[ia];
    const an = g_ent_name[ia][0..g_ent_name_len[ia]];
    const bn = g_ent_name[ib][0..g_ent_name_len[ib]];
    return std.ascii.orderIgnoreCase(an, bn) == .lt;
}

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

/// Navigate the browser to the folder containing `full_path` (an asset path
/// under `assets_path`). Used to reveal an asset double-clicked in the
/// inspector. No-op if the asset is not under this project's assets dir.
fn revealTo(assets_path: []const u8, full_path: []const u8) void {
    if (!std.mem.startsWith(u8, full_path, assets_path)) return;
    var rest = full_path[assets_path.len..];
    if (rest.len > 0 and rest[0] == '/') rest = rest[1..];
    const sub = if (std.mem.lastIndexOfScalar(u8, rest, '/')) |sep| rest[0..sep] else "";
    const len = @min(sub.len, current_subdir_buf.len);
    @memcpy(current_subdir_buf[0..len], sub[0..len]);
    current_subdir_len = len;
    last_click_name_len = 0;
}

fn fullPathFor(name: []const u8, browse_path: []const u8, buf: []u8) []const u8 {
    const sub = currentSubdir();
    return if (sub.len > 0)
        std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch ""
    else
        std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch "";
}

fn navigateBrowserItems(go_prev: bool, browse_path: []const u8) void {
    if (g_nav_count == 0) return;
    const sel = EditorState.selected_asset_path;

    // Find current selection index in nav list. ".." is at virtual index -1.
    var cur: ?usize = null;
    if (sel) |s| {
        for (0..g_nav_count) |i| {
            var p_buf: [1024]u8 = undefined;
            const p = std.fmt.bufPrint(&p_buf, "{s}/{s}", .{ browse_path, g_nav_names[i][0..g_nav_name_lens[i]] }) catch continue;
            if (std.mem.eql(u8, p, s)) {
                cur = i;
                break;
            }
        }
    }

    const new_idx: usize = blk: {
        if (cur) |ci| {
            if (go_prev) {
                break :blk if (ci > 0) ci - 1 else 0;
            } else {
                break :blk if (ci + 1 < g_nav_count) ci + 1 else g_nav_count - 1;
            }
        }
        break :blk 0;
    };

    var p_buf: [1024]u8 = undefined;
    const p = std.fmt.bufPrint(&p_buf, "{s}/{s}", .{ browse_path, g_nav_names[new_idx][0..g_nav_name_lens[new_idx]] }) catch return;
    EditorState.selectAsset(p);
}

/// Draw the asset browser panel with file tiles and navigation.
pub fn draw() void {
    var outer = gui.box(@src(), .{}, .{
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
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();

        gui.label(@src(), "Asset Browser", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });

        // Show the path relative to the project (from `assets/` onward); the
        // full project path is redundant and clutters the header. File changes
        // are picked up by the file watcher, so there is no Refresh button.
        if (EditorState.project_path != null) {
            if (current_subdir_len > 0) {
                gui.label(@src(), "  assets/{s}", .{currentSubdir()}, .{ .gravity_y = 0.5, .expand = .horizontal });
            } else {
                gui.label(@src(), "  assets", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
            }
        }
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
    if (EditorState.takeRevealRequest()) |rp| revealTo(assets_path, rp);

    var browse_path_buf: [1024]u8 = undefined;
    const browse_path: []const u8 = if (current_subdir_len > 0)
        std.fmt.bufPrint(&browse_path_buf, "{s}/{s}", .{ assets_path, currentSubdir() }) catch assets_path
    else
        assets_path;

    // Publish the current folder so folder-agnostic actions (e.g. "Create
    // Prefab" from the Scene Tree) write into the folder on screen.
    EditorState.setActiveBrowseDir(browse_path);

    // Handle keyboard events
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
            navigateBrowserItems(ke.code == .up or ke.code == .left, browse_path);
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

    // Handle delete confirmation dialog
    handleDeleteDialog();

    var scroll = gui.scrollArea(@src(), .{ .vertical = .auto }, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
    defer scroll.deinit();

    var dir = std.Io.Dir.cwd().openDir(gui.io, browse_path, .{ .iterate = true }) catch {
        gui.label(@src(), "No assets folder found. Create {s}/assets.", .{proj_path}, .{ .padding = .all(8) });
        return;
    };
    defer dir.close(gui.io);

    var flex = gui.flexbox(@src(), .{}, .{ .expand = .horizontal, .padding = .all(4) });
    defer flex.deinit();

    g_drag_hover_idx = null;

    // Reset nav list; will be rebuilt during tile rendering for next frame's keyboard handler
    var new_nav_count: usize = 0;

    var entry_idx: usize = 0;
    if (current_subdir_len > 0) {
        const up_hovered = EditorState.drag_kind == .asset and g_drag_hover_idx == 99999;
        var tile = gui.box(@src(), .{}, .{
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

        for (gui.events()) |*e| {
            if (!gui.eventMatchSimple(e, tile.data())) continue;
            switch (e.evt) {
                .mouse => |me| {
                    if (me.action == .position and EditorState.drag_kind == .asset) {
                        g_drag_hover_idx = 99999;
                    }
                    if (me.action == .press and me.button == .left) {
                        e.handle(@src(), tile.data());
                        const now = gui.frameTimeNS();
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
                                        std.Io.Dir.rename(std.Io.Dir.cwd(), src_path, std.Io.Dir.cwd(), full_dest, gui.io) catch {};
                                        var src_meta_buf: [1024 + 5]u8 = undefined;
                                        var dest_meta_buf: [1024 + 5]u8 = undefined;
                                        const src_meta = std.fmt.bufPrint(&src_meta_buf, "{s}.meta", .{src_path}) catch "";
                                        const dest_meta = std.fmt.bufPrint(&dest_meta_buf, "{s}.meta", .{full_dest}) catch "";
                                        if (src_meta.len > 0 and dest_meta.len > 0) {
                                            std.Io.Dir.rename(std.Io.Dir.cwd(), src_meta, std.Io.Dir.cwd(), dest_meta, gui.io) catch {};
                                        }
                                        EditorState.clearDrag();
                                        EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
                                        gui.refresh(null, @src(), null);
                                    }
                                }
                            }
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
            const n = @min(e.name.len, g_ent_name[ent_count].len);
            @memcpy(g_ent_name[ent_count][0..n], e.name[0..n]);
            g_ent_name_len[ent_count] = @intCast(n);
            g_ent_is_dir[ent_count] = e.kind == .directory;
            g_ent_order[ent_count] = ent_count;
            ent_count += 1;
        }
    }
    std.mem.sort(usize, g_ent_order[0..ent_count], {}, entryLessThan);

    for (g_ent_order[0..ent_count]) |ei| {
        const entry = .{ .name = g_ent_name[ei][0..g_ent_name_len[ei]], .is_dir = g_ent_is_dir[ei] };
        defer entry_idx += 1;

        const is_dir = entry.is_dir;

        // Collect into nav list for next frame's keyboard handler
        if (new_nav_count < NAV_MAX) {
            const n = @min(entry.name.len, g_nav_names[new_nav_count].len);
            @memcpy(g_nav_names[new_nav_count][0..n], entry.name[0..n]);
            g_nav_name_lens[new_nav_count] = n;
            g_nav_is_dir[new_nav_count] = is_dir;
            new_nav_count += 1;
        }
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
            gui.entypo.folder
        else switch (desc.icon_hint) {
            .document => gui.entypo.text_document,
            .code => gui.entypo.code,
            .image => gui.entypo.image,
            .sound => gui.entypo.sound,
            .model => gui.entypo.layers,
            .material => gui.entypo.colours,
            .data => gui.entypo.database,
        };

        const is_selected = if (EditorState.selected_asset_path) |sel_path| blk: {
            var asset_path_check: [1024]u8 = undefined;
            const ap = fullPathFor(entry.name, browse_path, &asset_path_check);
            break :blk std.mem.eql(u8, sel_path, ap);
        } else false;

        const is_drag_target = is_dir and EditorState.drag_kind == .asset and g_drag_hover_idx == entry_idx;
        var tile = gui.box(@src(), .{}, .{
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
            const cxt = gui.context(@src(), .{
                .rect = tile.data().borderRectScale().r,
            }, .{ .id_extra = entry_idx });
            defer cxt.deinit();

            if (cxt.activePoint()) |cp| {
                var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = entry_idx });
                defer fw.deinit();

                if (!is_dir and desc.open_mode != .none) {
                    const open_label = switch (desc.open_mode) {
                        .internal_editor => "Open",
                        .external_editor => "Open in External Editor",
                        .none => unreachable,
                    };
                    if (gui.menuItemLabel(@src(), open_label, .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        openAsset(proj_path, browse_path, entry.name, desc.open_mode);
                    }
                }

                // A scene asset can also be instantiated as a linked prefab
                // instance in the current scene.
                if (!is_dir and asset_type == .scene) {
                    if (gui.menuItemLabel(@src(), "Instantiate into Scene", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        instantiatePrefabFile(proj_path, entry.name);
                    }
                }

                // Rename option for files and directories
                if (gui.menuItemLabel(@src(), "Rename", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    var asset_path: [1024]u8 = undefined;
                    const ap = fullPathFor(entry.name, browse_path, &asset_path);
                    EditorState.startRenameAsset(ap);
                }

                // Delete option for files and directories
                if (gui.menuItemLabel(@src(), "Delete", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
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
                    if (gui.menuItemLabel(@src(), "Reimport Asset", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        editor.asset_importer.importAssetForce(gui.io, gui.currentWindow().arena(), proj_path, reimport_path);
                    }
                }

                if (gui.menuItemLabel(@src(), "Reveal in file manager", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                    fw.close();
                    AssetActions.revealInFileManager(browse_path, entry.name);
                }

                if (!is_dir) {
                    if (gui.menuItemLabel(@src(), "Copy Absolute Path", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        var copy_abs_raw_buf: [1024]u8 = undefined;
                        const copy_abs_raw = std.fmt.bufPrint(&copy_abs_raw_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                        var copy_abs_resolved_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
                        const copy_abs_len = std.Io.Dir.realPathFile(std.Io.Dir.cwd(), gui.io, copy_abs_raw, &copy_abs_resolved_buf) catch 0;
                        const copy_abs = if (copy_abs_len > 0) copy_abs_resolved_buf[0..copy_abs_len] else copy_abs_raw;
                        gui.clipboardTextSet(copy_abs);
                    }
                    if (gui.menuItemLabel(@src(), "Copy Relative Path", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                        fw.close();
                        const sub = currentSubdir();
                        var copy_rel_buf: [1024]u8 = undefined;
                        const copy_rel = if (sub.len > 0)
                            std.fmt.bufPrint(&copy_rel_buf, "assets/{s}/{s}", .{ sub, entry.name }) catch ""
                        else
                            std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{entry.name}) catch "";
                        gui.clipboardTextSet(copy_rel);
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
                        if (gui.menuItemLabel(@src(), "Copy GUID", .{}, .{ .expand = .horizontal, .id_extra = entry_idx }) != null) {
                            fw.close();
                            gui.clipboardTextSet(maybe_guid_str);
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
                            const same_name = std.mem.eql(u8, last_click_name_buf[0..last_click_name_len], entry.name);
                            if (same_name and now - last_click_ns < 500 * std.time.ns_per_ms) {
                                if (is_dir) {
                                    enterSubdir(entry.name);
                                } else if (desc.open_mode != .none) {
                                    openAsset(proj_path, browse_path, entry.name, desc.open_mode);
                                }
                            } else if (is_dir) {
                                // Single-click on folder: select it so F2 / arrow keys work.
                                var folder_sel_buf: [1024]u8 = undefined;
                                const folder_sel = fullPathFor(entry.name, browse_path, &folder_sel_buf);
                                EditorState.selectAsset(folder_sel);
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
        }

        gui.icon(@src(), "tile_icon", icon_bytes, .{}, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = 32, .h = 32 },
            .id_extra = entry_idx,
        });

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
            gui.label(@src(), "{s}", .{entry.name}, .{
                .gravity_x = 0.5,
                .min_size_content = .{ .w = 64 },
                .id_extra = entry_idx,
            });
        }

        if (is_dir) {
            const drag_compatible = EditorState.drag_kind == .asset;
            if (drag_compatible) {
                for (gui.events()) |*e| {
                    if (!gui.eventMatchSimple(e, tile.data())) continue;
                    switch (e.evt) {
                        .mouse => |me| {
                            if (me.action == .position) {
                                g_drag_hover_idx = entry_idx;
                            }
                            if (me.action == .release and me.button == .left) {
                                e.handle(@src(), tile.data());
                                const src_path = EditorState.dragAssetPath();
                                if (src_path.len > 0) {
                                    var dest_buf: [1024]u8 = undefined;
                                    const dest_path = std.fmt.bufPrint(&dest_buf, "{s}/{s}", .{ browse_path, entry.name }) catch "";
                                    if (dest_path.len > 0) {
                                        if (std.mem.lastIndexOfScalar(u8, src_path, '/')) |sep| {
                                            const file_name = src_path[sep + 1 ..];
                                            var full_dest_buf: [1024]u8 = undefined;
                                            const full_dest = std.fmt.bufPrint(&full_dest_buf, "{s}/{s}", .{ dest_path, file_name }) catch "";
                                            if (full_dest.len > 0) {
                                                std.Io.Dir.rename(std.Io.Dir.cwd(), src_path, std.Io.Dir.cwd(), full_dest, gui.io) catch {};
                                                // Also move .meta file
                                                var src_meta_buf: [1024 + 5]u8 = undefined;
                                                var dest_meta_buf: [1024 + 5]u8 = undefined;
                                                const src_meta = std.fmt.bufPrint(&src_meta_buf, "{s}.meta", .{src_path}) catch "";
                                                const dest_meta = std.fmt.bufPrint(&dest_meta_buf, "{s}.meta", .{full_dest}) catch "";
                                                if (src_meta.len > 0 and dest_meta.len > 0) {
                                                    std.Io.Dir.rename(std.Io.Dir.cwd(), src_meta, std.Io.Dir.cwd(), dest_meta, gui.io) catch {};
                                                }
                                                EditorState.clearDrag();
                                                EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
                                                gui.refresh(null, @src(), null);
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

    // Commit nav list for next frame's arrow-key handler
    g_nav_count = new_nav_count;
    g_nav_has_up = current_subdir_len > 0;

    // Drop a dragged scene object onto the browser to save it (and its
    // children) as a prefab asset in the current folder.
    if (EditorState.drag_kind == .game_object) {
        for (gui.events()) |*e| {
            if (!gui.eventMatchSimple(e, outer.data())) continue;
            if (e.evt == .mouse) {
                const me = e.evt.mouse;
                if (me.action == .release and me.button == .left) {
                    e.handle(@src(), outer.data());
                    _ = EditorState.createPrefabFromObject(gui.frameTimeNS(), gui.io, EditorState.drag_object_idx);
                    EditorState.clearDrag();
                    gui.refresh(null, @src(), null);
                }
            }
        }
    }

    // Empty area context menu
    {
        const cxt = gui.context(@src(), .{ .rect = outer.data().borderRectScale().r }, .{});
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
                const sub = currentSubdir();
                var copy_rel_buf: [1024]u8 = undefined;
                const copy_rel = if (sub.len > 0)
                    std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{sub}) catch "assets"
                else
                    "assets";
                gui.clipboardTextSet(copy_rel);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), "New Prefab", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                AssetActions.createNewPrefab(browse_path);
            }
            if (gui.menuItemLabel(@src(), "New Project Settings", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                AssetActions.createNewProjectSettings(browse_path);
            }
            if (gui.menuItemLabel(@src(), "New Input Actions", .{}, .{ .expand = .horizontal }) != null) {
                fw.close();
                AssetActions.createNewInputActions(browse_path);
            }
            for (engine.Material.presets, 0..) |preset, pi| {
                var label_buf: [64]u8 = undefined;
                const label = std.fmt.bufPrint(&label_buf, "New Material: {s}", .{preset.name}) catch continue;
                if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = pi }) != null) {
                    fw.close();
                    AssetActions.createNewMaterialFromPreset(browse_path, preset);
                }
            }
            for (EditorState.discovered_components[0..EditorState.discovered_count], 0..) |*def, di| {
                if (def.kind != .data_asset) continue;
                var label_buf: [128]u8 = undefined;
                const label = std.fmt.bufPrint(&label_buf, "New {s}", .{def.displayName()}) catch continue;
                if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = 1000 + di }) != null) {
                    fw.close();
                    AssetActions.createNewDataAsset(browse_path, def);
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
                std.Io.Dir.cwd().deleteFile(gui.io, del_path) catch {
                    std.Io.Dir.cwd().deleteDir(gui.io, del_path) catch {};
                };
                var meta_buf: [1024 + 5]u8 = undefined;
                const meta_path = std.fmt.bufPrint(&meta_buf, "{s}.meta", .{del_path}) catch "";
                if (meta_path.len > 0) {
                    std.Io.Dir.cwd().deleteFile(gui.io, meta_path) catch {};
                }
                if (EditorState.selected_asset_path) |sel| {
                    if (std.mem.eql(u8, sel, del_path)) {
                        EditorState.clearSelectedAsset();
                    }
                }
                EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
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
    gui.dialog(@src(), .{}, .{
        .title = "Delete Asset",
        .message = msg,
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

fn openAsset(proj_path: []const u8, browse_path: []const u8, file_name: []const u8, open_mode: editor.asset_registry.OpenMode) void {
    _ = proj_path;
    switch (open_mode) {
        .external_editor => {
            AssetActions.openExternal(browse_path, file_name);
            return;
        },
        .none => return,
        .internal_editor => {},
    }

    // Open the asset as a document tab. Scenes/prefabs route to
    // the scene-editing surface; everything else to its dedicated editor.
    var path_buf: [1024]u8 = undefined;
    const full_path = fullPathFor(file_name, browse_path, &path_buf);
    const asset_type = editor.asset_registry.lookupByFilename(file_name);
    if (asset_type == .scene) {
        EditorState.selected_object = null;
        EditorState.clearSelectedAsset();
        Documents.openScene(full_path);
    } else {
        Documents.openAsset(full_path, asset_type);
    }
}

/// Instantiate a scene/prefab asset as a linked subtree in the active scene.
fn instantiatePrefabFile(proj_path: []const u8, file_name: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    const sub = currentSubdir();
    const full_path = if (sub.len > 0)
        std.fmt.bufPrint(&path_buf, "{s}/assets/{s}/{s}", .{ proj_path, sub, file_name }) catch return
    else
        std.fmt.bufPrint(&path_buf, "{s}/assets/{s}", .{ proj_path, file_name }) catch return;
    _ = EditorState.instantiatePrefab(gui.frameTimeNS(), gui.io, full_path);
}

// Asset open/reveal/create helpers live in AssetActions.zig.
