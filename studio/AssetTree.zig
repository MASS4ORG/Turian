//! Flat, parent-indexed recursive listing of a project's `assets/` directory
//! (folders and files, `.meta` sidecars skipped) — the same flat-array shape
//! `EditorState.objects` uses for the scene hierarchy, so it can back
//! `TreeView` the same way `SceneTree.zig` does (see AssetBrowser.zig's
//! `FolderModel` / `FullTreeModel`).
//!
//! Rebuilt lazily whenever `EditorState.asset_refresh_generation` changes —
//! that counter is bumped by `refreshComponents`, which already runs after
//! every mutating asset operation (rename/delete/move/create) and on every
//! watcher-detected filesystem change, so `ensure()` is a cheap no-op on most
//! frames and callers can invoke it freely from `draw()`.

const std = @import("std");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

/// Upper bound on total folder+file nodes tracked at once. Scanning stops
/// silently once hit (same graceful-cap convention as `AssetBrowser`'s
/// single-directory `MAX_ENTRIES`).
pub const MAX_NODES = 4096;

var node_parent: [MAX_NODES]i32 = undefined;
var node_is_dir: [MAX_NODES]bool = undefined;
var node_asset_type: [MAX_NODES]editor.AssetType = undefined;
var node_name: [MAX_NODES][256]u8 = undefined;
var node_name_len: [MAX_NODES]u16 = undefined;
var node_path: [MAX_NODES][1024]u8 = undefined;
var node_path_len: [MAX_NODES]u16 = undefined;
var node_count: usize = 0;

// Folder-only projection, recomputed alongside the full scan: lets a
// folder-only tree (the Grid+Tree sidebar) iterate without every row's
// `parentOf`/`count` having to skip file nodes each frame.
var folder_index_of_node: [MAX_NODES]i32 = undefined; // node idx -> folder-local idx, -1 if not a dir
var folder_node: [MAX_NODES]usize = undefined; // folder-local idx -> node idx
var folder_count: usize = 0;

var root_path_buf: [1024]u8 = undefined;
var root_path_len: usize = 0;

var built_generation: u64 = 0;
var built_for_project_buf: [1024]u8 = undefined;
var built_for_project_len: usize = 0;

// Per-directory scratch used while scanning (see `scanDir`'s doc comment for
// why reusing one global buffer across recursive calls is safe here).
const SCRATCH_CAP = 512;
var scratch_name: [SCRATCH_CAP][256]u8 = undefined;
var scratch_name_len: [SCRATCH_CAP]u16 = undefined;
var scratch_is_dir: [SCRATCH_CAP]bool = undefined;
var scratch_order: [SCRATCH_CAP]usize = undefined;

pub fn count() usize {
    return node_count;
}

pub fn parentOf(i: usize) i32 {
    return node_parent[i];
}

pub fn name(i: usize) []const u8 {
    return node_name[i][0..node_name_len[i]];
}

pub fn path(i: usize) []const u8 {
    return node_path[i][0..node_path_len[i]];
}

pub fn isDir(i: usize) bool {
    return node_is_dir[i];
}

pub fn assetType(i: usize) editor.AssetType {
    return node_asset_type[i];
}

/// The project's `assets` directory, i.e. the path stored on nodes with no
/// parent (`parentOf(i) == -1`).
pub fn rootPath() []const u8 {
    return root_path_buf[0..root_path_len];
}

/// Directory containing node `i` — its parent's path, or the assets root when
/// `i` is a top-level entry.
pub fn dirOf(i: usize) []const u8 {
    const p = node_parent[i];
    return if (p < 0) rootPath() else path(@intCast(p));
}

/// Linear search for the node whose path equals `p`, or null. O(n); callers
/// should cache the result rather than call this per row per frame.
pub fn indexOfPath(p: []const u8) ?usize {
    for (0..node_count) |i| {
        if (std.mem.eql(u8, path(i), p)) return i;
    }
    return null;
}

pub fn folderCount() usize {
    return folder_count;
}

/// Node index for folder-local index `fi`.
pub fn folderNode(fi: usize) usize {
    return folder_node[fi];
}

/// Folder-local index of node `node_idx`, or -1 if it is not a directory.
pub fn folderIndexOfNode(node_idx: usize) i32 {
    return folder_index_of_node[node_idx];
}

/// `TreeView`-shaped `parentOf` over the folder-only projection: a folder's
/// parent is always itself a folder (or root), so this never needs to skip
/// past a file node.
pub fn folderParentOf(fi: usize) i32 {
    const p = node_parent[folder_node[fi]];
    return if (p < 0) -1 else folder_index_of_node[@intCast(p)];
}

/// Rebuild from disk if the project changed or an asset-affecting event
/// happened since the last build (`EditorState.asset_refresh_generation`).
/// Cheap no-op otherwise — call freely every frame a tree view is visible.
pub fn ensure(io: std.Io) void {
    const proj = EditorState.project_path orelse {
        node_count = 0;
        folder_count = 0;
        root_path_len = 0;
        built_for_project_len = 0;
        return;
    };

    const same_project = std.mem.eql(u8, built_for_project_buf[0..built_for_project_len], proj);
    if (same_project and built_generation == EditorState.asset_refresh_generation) return;

    const n = @min(proj.len, built_for_project_buf.len);
    @memcpy(built_for_project_buf[0..n], proj[0..n]);
    built_for_project_len = n;
    built_generation = EditorState.asset_refresh_generation;

    const rp = std.fmt.bufPrint(&root_path_buf, "{s}/assets", .{proj}) catch {
        node_count = 0;
        folder_count = 0;
        root_path_len = 0;
        return;
    };
    root_path_len = rp.len;
    node_count = 0;
    scanDir(io, rootPath(), -1);

    folder_count = 0;
    for (0..node_count) |i| {
        if (node_is_dir[i]) {
            folder_index_of_node[i] = @intCast(folder_count);
            folder_node[folder_count] = i;
            folder_count += 1;
        } else {
            folder_index_of_node[i] = -1;
        }
    }
}

fn scratchLessThan(_: void, a: usize, b: usize) bool {
    if (scratch_is_dir[a] != scratch_is_dir[b]) return scratch_is_dir[a];
    return std.ascii.orderIgnoreCase(scratch_name[a][0..scratch_name_len[a]], scratch_name[b][0..scratch_name_len[b]]) == .lt;
}

/// Depth-first scan of `dir_path` into the flat node arrays. Entries for the
/// current directory are enumerated and sorted into the module-level scratch
/// buffers, then fully copied into permanent per-node storage *before* any
/// recursive call is made — so nested calls reusing the same scratch buffers
/// never clobber data this level still needs. Kept iteration-heavy rather
/// than allocating per-call stack buffers, since directory depth is
/// unbounded and this runs on the main GUI thread.
fn scanDir(io: std.Io, dir_path: []const u8, parent: i32) void {
    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return;
    defer dir.close(io);

    var raw_count: usize = 0;
    var iter = dir.iterate();
    while (iter.next(io) catch null) |e| {
        if (e.kind != .directory and std.mem.endsWith(u8, e.name, ".meta")) continue;
        if (raw_count >= SCRATCH_CAP) break;
        const l = @min(e.name.len, scratch_name[raw_count].len);
        @memcpy(scratch_name[raw_count][0..l], e.name[0..l]);
        scratch_name_len[raw_count] = @intCast(l);
        scratch_is_dir[raw_count] = e.kind == .directory;
        scratch_order[raw_count] = raw_count;
        raw_count += 1;
    }
    std.mem.sort(usize, scratch_order[0..raw_count], {}, scratchLessThan);

    const start = node_count;
    for (scratch_order[0..raw_count]) |si| {
        if (node_count >= MAX_NODES) break;
        const idx = node_count;
        node_count += 1;

        node_parent[idx] = parent;
        node_is_dir[idx] = scratch_is_dir[si];
        const nl = scratch_name_len[si];
        @memcpy(node_name[idx][0..nl], scratch_name[si][0..nl]);
        node_name_len[idx] = nl;

        const ename = scratch_name[si][0..nl];
        const full = std.fmt.bufPrint(&node_path[idx], "{s}/{s}", .{ dir_path, ename }) catch ename;
        node_path_len[idx] = @intCast(full.len);

        node_asset_type[idx] = if (scratch_is_dir[si])
            .unknown
        else if (EditorState.assetDbReady())
            (if (EditorState.asset_db.findByPath(full)) |info| info.asset_type else editor.asset_registry.lookupByFilename(ename))
        else
            editor.asset_registry.lookupByFilename(ename);
    }
    const end = node_count;

    var i = start;
    while (i < end) : (i += 1) {
        if (node_is_dir[i]) scanDir(io, path(i), @intCast(i));
    }
}
