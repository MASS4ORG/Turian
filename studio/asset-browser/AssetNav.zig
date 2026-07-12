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

/// `browse_path/name`, written into `buf`.
pub fn fullPathFor(name: []const u8, browse_path: []const u8, buf: []u8) []const u8 {
    return std.fmt.bufPrint(buf, "{s}/{s}", .{ browse_path, name }) catch "";
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
                delete_dialog_result = response == .ok;
            }
        }.callafter,
    });
}
