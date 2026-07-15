//! Shared navigation state for the asset browser's three views (grid,
//! folder-tree sidebar, full tree): the "current folder" the grid/sidebar are
//! scoped to, double-click tracking, and the delete-confirmation dialog. Kept
//! separate from `AssetBrowser.zig` because all three views — and their
//! context menus in `AssetContextMenus.zig` — read and mutate this same
//! state, and `AssetBrowser.zig` is the top-level orchestrator, not the
//! natural home for it.

const std = @import("std");
const gui = @import("gui");
const EditorState = @import("../services/EditorState.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

// ── Double-click tracking (grid tiles, both real entries and the `..` tile) ─

pub var last_click_name_buf: [256]u8 = undefined;
pub var last_click_name_len: usize = 0;
pub var last_click_ns: i128 = 0;

// ── Current folder ───────────────────────────────────────────────────────────

var current_subdir_buf: [512]u8 = undefined;
pub var current_subdir_len: usize = 0;

pub fn currentSubdir() []const u8 {
    return current_subdir_buf[0..current_subdir_len];
}

/// Set the current folder directly to `sub` (a path relative to the assets
/// root, no leading slash) — used by the folder tree's row-click navigation
/// and its always-visible root row.
pub fn setCurrentSubdir(sub: []const u8) void {
    const len = @min(sub.len, current_subdir_buf.len);
    @memcpy(current_subdir_buf[0..len], sub[0..len]);
    current_subdir_len = len;
    last_click_name_len = 0;
}

pub fn enterSubdir(name: []const u8) void {
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

pub fn goUp() void {
    const sub = currentSubdir();
    if (std.mem.lastIndexOfScalar(u8, sub, '/')) |sep| {
        current_subdir_len = sep;
    } else {
        current_subdir_len = 0;
    }
    last_click_name_len = 0;
}

/// Navigate to the folder containing `full_path` (an asset path under
/// `assets_path`). Used to reveal an asset double-clicked in the inspector.
/// No-op if the asset is not under this project's assets dir.
pub fn revealTo(assets_path: []const u8, full_path: []const u8) void {
    if (!std.mem.startsWith(u8, full_path, assets_path)) return;
    var rest = full_path[assets_path.len..];
    if (rest.len > 0 and rest[0] == '/') rest = rest[1..];
    const sub = if (std.mem.lastIndexOfScalar(u8, rest, '/')) |sep| rest[0..sep] else "";
    setCurrentSubdir(sub);
}

/// Draw the current folder as clickable breadcrumb segments:
/// "assets" (root) followed by one button per path component, each jumping
/// straight to that ancestor folder. Replaces the old static path label.
pub fn drawBreadcrumb() void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .gravity_y = 0.5 });
    defer row.deinit();

    if (gui.button(@src(), "assets", .{}, .{
        .gravity_y = 0.5,
        .padding = .{ .x = 4, .y = 2 },
        .margin = .{ .x = 4 },
        .style = if (current_subdir_len == 0) .highlight else .content,
    })) {
        setCurrentSubdir("");
    }

    const sub = currentSubdir();
    var start: usize = 0;
    var seg: usize = 0;
    while (start < sub.len) : (seg += 1) {
        const end = if (std.mem.indexOfScalarPos(u8, sub, start, '/')) |sep| sep else sub.len;

        gui.label(@src(), "/", .{}, .{ .gravity_y = 0.5, .id_extra = seg });

        // `sub[start..end]` is a slice into `current_subdir_buf` itself, so
        // navigating here only needs to truncate the length (like `goUp`) —
        // the bytes up to `end` are already the correct prefix. Avoids
        // `setCurrentSubdir` aliasing its own buffer as src and dest.
        if (gui.button(@src(), sub[start..end], .{}, .{
            .gravity_y = 0.5,
            .padding = .{ .x = 4, .y = 2 },
            .margin = .{ .x = 4 },
            .id_extra = seg,
            .style = if (end == sub.len) .highlight else .content,
        })) {
            current_subdir_len = end;
            last_click_name_len = 0;
        }

        start = end + 1;
    }
}

/// `browse_path/name`, written into `buf`.
pub fn fullPathFor(name: []const u8, browse_path: []const u8, buf: []u8) []const u8 {
    return std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch "";
}

// ── Arrow-key entry navigation (grid tiles) ─────────────────────────────────
//
// `AssetGridView.draw` records its folder listing here each frame (in display
// order) so `navigateBrowserItems` — driven by `AssetBrowser.zig`'s arrow-key
// handler — can move the selection to the previous/next entry. Used the NEXT
// frame so the keyboard handler can reference the previous listing.

const NAV_MAX = 256;
var nav_names: [NAV_MAX][256]u8 = undefined;
var nav_name_lens: [NAV_MAX]usize = [_]usize{0} ** NAV_MAX;
var nav_count: usize = 0;
var new_nav_count: usize = 0;

/// Start collecting this frame's entry listing. Call once before iterating
/// the folder's entries.
pub fn beginNavList() void {
    new_nav_count = 0;
}

/// Record one entry, in listing order, for the next frame's arrow-key handler.
pub fn recordNavEntry(name: []const u8) void {
    if (new_nav_count >= NAV_MAX) return;
    const n = @min(name.len, nav_names[new_nav_count].len);
    @memcpy(nav_names[new_nav_count][0..n], name[0..n]);
    nav_name_lens[new_nav_count] = n;
    new_nav_count += 1;
}

/// Commit this frame's listing (collected via `recordNavEntry`) so the next
/// frame's arrow-key handler sees it.
pub fn commitNavList() void {
    nav_count = new_nav_count;
}

/// Move the asset selection to the previous/next entry in the current
/// folder's alphabetical listing — driven by the panel's arrow-key handler.
pub fn navigateBrowserItems(go_prev: bool, browse_path: []const u8) void {
    if (nav_count == 0) return;
    const sel = EditorState.selected_asset_path;

    var cur: ?usize = null;
    if (sel) |s| {
        for (0..nav_count) |i| {
            var p_buf: [1024]u8 = undefined;
            const p = std.fmt.bufPrint(&p_buf, "{s}/{s}", .{ browse_path, nav_names[i][0..nav_name_lens[i]] }) catch continue;
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
                break :blk if (ci + 1 < nav_count) ci + 1 else nav_count - 1;
            }
        }
        break :blk 0;
    };

    var p_buf: [1024]u8 = undefined;
    const p = std.fmt.bufPrint(&p_buf, "{s}/{s}", .{ browse_path, nav_names[new_idx][0..nav_name_lens[new_idx]] }) catch return;
    EditorState.selectAsset(p);
}

// ── Delete confirmation dialog ───────────────────────────────────────────────
//
// Shared by the grid tiles' Delete menu item and both asset trees' Delete
// (context menu / Del key via `TreeView.Model.removeRequested`), so only one
// dialog implementation exists and all three views behave identically.

var pending_delete_path: [1024]u8 = undefined;
var pending_delete_len: usize = 0;
var show_delete_dialog: bool = false;
var delete_dialog_result: ?bool = null;

/// Arm the shared delete-confirmation dialog for `path`.
pub fn requestDelete(path: []const u8) void {
    const n = @min(path.len, pending_delete_path.len);
    @memcpy(pending_delete_path[0..n], path[0..n]);
    pending_delete_len = n;
    show_delete_dialog = true;
}

pub fn handleDeleteDialog() void {
    {
        const result = &delete_dialog_result;
        if (result.*) |confirmed| {
            show_delete_dialog = false;
            result.* = null;
            if (confirmed) {
                const del_path = pending_delete_path[0..pending_delete_len];
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
            pending_delete_len = 0;
        }
    }

    if (!show_delete_dialog) return;
    if (pending_delete_len == 0) return;

    const path = pending_delete_path[0..pending_delete_len];
    const file_name = if (std.mem.lastIndexOfScalar(u8, path, '/')) |sep|
        path[sep + 1 ..]
    else
        path;

    const msg = StudioLocale.trArgs("Delete '{name}' permanently?", &.{.{ .name = "name", .value = .{ .text = file_name } }});
    gui.dialog(@src(), .{}, .{
        .title = tr("Delete Asset"),
        .message = msg,
        .ok_label = tr("Delete"),
        .cancel_label = tr("Cancel"),
        .default = .cancel,
        .callafterFn = struct {
            fn callafter(_: gui.Id, response: gui.enums.DialogResponse) !void {
                delete_dialog_result = response == .ok;
            }
        }.callafter,
    });
}
