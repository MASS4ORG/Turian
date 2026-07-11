//! Context-menu content shared by all three asset browser views (grid tiles,
//! folder tree, full tree): "New <asset kind>" creation items, and the
//! per-asset Open/Instantiate/Reimport/Reveal/Copy-path/Copy-GUID items. Each
//! view still draws its own Rename/Delete (the grid manages inline rename
//! state itself; `TreeView` provides Rename/Delete generically for tree
//! rows) — this file covers everything else, so the three views can't drift
//! out of sync with each other.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const AssetActions = @import("AssetActions.zig");
const Documents = @import("Documents.zig");
const PreviewSystem = @import("PreviewSystem.zig");
const AssetNav = @import("AssetNav.zig");

pub fn iconForHint(hint: editor.asset_registry.IconHint) []const u8 {
    return switch (hint) {
        .document => gui.entypo.text_document,
        .code => gui.entypo.code,
        .image => gui.entypo.image,
        .sound => gui.entypo.sound,
        .model => gui.entypo.layers,
        .material => gui.entypo.colours,
        .data => gui.entypo.database,
        .font => gui.entypo.text,
    };
}

/// Open the asset as a document tab. Scenes/prefabs route to the
/// scene-editing surface; everything else to its dedicated editor.
pub fn openAsset(browse_path: []const u8, file_name: []const u8, open_mode: editor.asset_registry.OpenMode) void {
    switch (open_mode) {
        .external_editor => {
            AssetActions.openExternal(browse_path, file_name);
            return;
        },
        .none => return,
        .internal_editor => {},
    }

    var path_buf: [1024]u8 = undefined;
    const full_path = AssetNav.fullPathFor(file_name, browse_path, &path_buf);
    const asset_type = editor.asset_registry.lookupByFilename(file_name);
    if (asset_type == .scene) {
        EditorState.selected_object = null;
        EditorState.clearSelectedAsset();
        Documents.openScene(full_path);
    } else {
        Documents.openAsset(full_path, asset_type);
    }
}

// ── Sub-asset count cache ────────────────────────────────────────────────────
//
// Cached sub-asset count per model path, so the grid's expand-arrow check
// (drawn for every visible model tile, every frame) doesn't re-read+parse
// that model's `.meta` every frame — the exact same class of bug fixed in
// `PreviewSystem` for thumbnails. Explicitly dropped by
// `invalidatePreviewAndSubAssets` on reimport; otherwise a model's sub-asset
// count never changes without one.
const MAX_SUBCOUNT_CACHE = 128;
var subcount_paths: [MAX_SUBCOUNT_CACHE][1024]u8 = undefined;
var subcount_lens: [MAX_SUBCOUNT_CACHE]usize = [_]usize{0} ** MAX_SUBCOUNT_CACHE;
var subcount_vals: [MAX_SUBCOUNT_CACHE]usize = undefined;
var subcount_count: usize = 0;
var subcount_next: usize = 0; // FIFO eviction cursor once full

pub fn cachedSubAssetCount(path: []const u8) ?usize {
    for (0..subcount_count) |i| {
        if (std.mem.eql(u8, subcount_paths[i][0..subcount_lens[i]], path)) return subcount_vals[i];
    }
    return null;
}

pub fn setSubAssetCount(path: []const u8, n: usize) void {
    for (0..subcount_count) |i| {
        if (std.mem.eql(u8, subcount_paths[i][0..subcount_lens[i]], path)) {
            subcount_vals[i] = n;
            return;
        }
    }
    const slot = if (subcount_count < MAX_SUBCOUNT_CACHE) blk: {
        const s = subcount_count;
        subcount_count += 1;
        break :blk s;
    } else blk: {
        const s = subcount_next;
        subcount_next = (subcount_next + 1) % MAX_SUBCOUNT_CACHE;
        break :blk s;
    };
    const len = @min(path.len, subcount_paths[slot].len);
    @memcpy(subcount_paths[slot][0..len], path[0..len]);
    subcount_lens[slot] = len;
    subcount_vals[slot] = n;
}

fn invalidateSubAssetCount(path: []const u8) void {
    for (0..subcount_count) |i| {
        if (std.mem.eql(u8, subcount_paths[i][0..subcount_lens[i]], path)) {
            subcount_lens[i] = 0;
            return;
        }
    }
}

/// After an explicit reimport, drop the cached preview for `asset_path` and
/// every sub-asset it lists — a model's sub-asset GUIDs stay stable across
/// reimports (so referencing scenes don't break), but their *content* can
/// change, and `PreviewSystem.imageSourceForGuid` has no `.meta`/source_hash
/// of its own to notice that automatically (see its doc comment).
pub fn invalidatePreviewAndSubAssets(asset_path: []const u8) void {
    if (!EditorState.assetDbReady()) return;
    const info = EditorState.asset_db.findByPath(asset_path) orelse return;
    var guid_buf: [36]u8 = undefined;
    PreviewSystem.invalidate(info.guid.toString(&guid_buf));

    var meta_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer meta_arena.deinit();
    const meta = editor.asset_meta.readMeta(gui.io, meta_arena.allocator(), asset_path);
    for (meta.sub_assets) |sub| {
        var sub_guid_buf: [36]u8 = undefined;
        PreviewSystem.invalidate(sub.guid.toString(&sub_guid_buf));
    }
    invalidateSubAssetCount(asset_path);
}

/// "New <asset kind>" menu items, scoped to create inside `browse_path`.
/// Shared by the grid's empty-area menu, the folder tree's per-folder menu,
/// and the full tree's per-folder menu. `id_base` keeps `id_extra`s from
/// colliding when a caller draws more than one of these in the same menu.
pub fn drawCreateAssetMenuItems(fw: *gui.FloatingMenuWidget, browse_path: []const u8, id_base: usize) void {
    if (gui.menuItemLabel(@src(), "New Folder", .{}, .{ .expand = .horizontal, .id_extra = id_base + 20 }) != null) {
        fw.close();
        AssetActions.createNewFolder(browse_path);
    }
    if (gui.menuItemLabel(@src(), "New Prefab", .{}, .{ .expand = .horizontal, .id_extra = id_base }) != null) {
        fw.close();
        AssetActions.createNewPrefab(browse_path);
    }
    if (gui.menuItemLabel(@src(), "New Project Settings", .{}, .{ .expand = .horizontal, .id_extra = id_base + 1 }) != null) {
        fw.close();
        AssetActions.createNewProjectSettings(browse_path);
    }
    if (gui.menuItemLabel(@src(), "New Input Actions", .{}, .{ .expand = .horizontal, .id_extra = id_base + 2 }) != null) {
        fw.close();
        AssetActions.createNewInputActions(browse_path);
    }
    if (gui.menuItemLabel(@src(), "New UI Document", .{}, .{ .expand = .horizontal, .id_extra = id_base + 3 }) != null) {
        fw.close();
        AssetActions.createNewUiDocument(browse_path);
    }
    for (engine.Material.presets, 0..) |preset, pi| {
        var label_buf: [64]u8 = undefined;
        const label = std.fmt.bufPrint(&label_buf, "New Material: {s}", .{preset.name}) catch continue;
        if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = id_base + 10 + pi }) != null) {
            fw.close();
            AssetActions.createNewMaterialFromPreset(browse_path, preset);
        }
    }
    for (EditorState.discovered_components[0..EditorState.discovered_count], 0..) |*def, di| {
        if (def.kind != .data_asset) continue;
        var label_buf: [128]u8 = undefined;
        const label = std.fmt.bufPrint(&label_buf, "New {s}", .{def.displayName()}) catch continue;
        if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = id_base + 1000 + di }) != null) {
            fw.close();
            AssetActions.createNewDataAsset(browse_path, def);
        }
    }
}

/// Open/Instantiate/Reimport/Reveal/Copy-path/Copy-GUID menu items for a
/// single asset at `browse_path`/`file_name`.
pub fn drawAssetExtraMenuItems(
    fw: *gui.FloatingMenuWidget,
    proj_path: []const u8,
    browse_path: []const u8,
    file_name: []const u8,
    is_dir: bool,
    asset_type: editor.AssetType,
    id_extra: usize,
) void {
    const desc = editor.asset_registry.get(asset_type);

    if (!is_dir and desc.open_mode != .none) {
        const open_label = switch (desc.open_mode) {
            .internal_editor => "Open",
            .external_editor => "Open in External Editor",
            .none => unreachable,
        };
        if (gui.menuItemLabel(@src(), open_label, .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
            fw.close();
            openAsset(browse_path, file_name, desc.open_mode);
        }
    }

    // A scene asset can also be instantiated as a linked prefab instance in
    // the current scene.
    if (!is_dir and asset_type == .scene) {
        if (gui.menuItemLabel(@src(), "Instantiate into Scene", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
            fw.close();
            var full_buf: [1024]u8 = undefined;
            if (std.fmt.bufPrint(&full_buf, "{s}/{s}", .{ browse_path, file_name })) |full| {
                _ = EditorState.instantiatePrefab(gui.frameTimeNS(), gui.io, full);
            } else |_| {}
        }
    }

    if (!is_dir) {
        var reimport_path_buf: [1024]u8 = undefined;
        const reimport_path = std.fmt.bufPrint(&reimport_path_buf, "{s}/{s}", .{ browse_path, file_name }) catch "";
        if (gui.menuItemLabel(@src(), "Reimport Asset", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
            fw.close();
            editor.asset_importer.importAssetForce(gui.io, gui.currentWindow().arena(), proj_path, reimport_path);
            invalidatePreviewAndSubAssets(reimport_path);
        }
    }

    if (gui.menuItemLabel(@src(), "Reveal in file manager", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
        fw.close();
        AssetActions.revealInFileManager(browse_path, file_name);
    }

    if (!is_dir) {
        if (gui.menuItemLabel(@src(), "Copy Absolute Path", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
            fw.close();
            var raw_buf: [1024]u8 = undefined;
            const raw = std.fmt.bufPrint(&raw_buf, "{s}/{s}", .{ browse_path, file_name }) catch "";
            var resolved_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
            const resolved_len = std.Io.Dir.realPathFile(std.Io.Dir.cwd(), gui.io, raw, &resolved_buf) catch 0;
            const resolved = if (resolved_len > 0) resolved_buf[0..resolved_len] else raw;
            gui.clipboardTextSet(resolved);
        }
        if (gui.menuItemLabel(@src(), "Copy Relative Path", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
            fw.close();
            var assets_buf: [1024]u8 = undefined;
            const assets_path = std.fmt.bufPrint(&assets_buf, "{s}/assets", .{proj_path}) catch "";
            var rel_dir: []const u8 = "";
            if (assets_path.len > 0 and std.mem.startsWith(u8, browse_path, assets_path)) {
                rel_dir = browse_path[assets_path.len..];
                if (rel_dir.len > 0 and rel_dir[0] == '/') rel_dir = rel_dir[1..];
            }
            var copy_rel_buf: [1024]u8 = undefined;
            const copy_rel = if (rel_dir.len > 0)
                std.fmt.bufPrint(&copy_rel_buf, "assets/{s}/{s}", .{ rel_dir, file_name }) catch ""
            else
                std.fmt.bufPrint(&copy_rel_buf, "assets/{s}", .{file_name}) catch "";
            gui.clipboardTextSet(copy_rel);
        }

        var guid_buf: [36]u8 = undefined;
        const maybe_guid_str = if (EditorState.assetDbReady()) blk: {
            var gp_buf: [1024]u8 = undefined;
            const gp = std.fmt.bufPrint(&gp_buf, "{s}/{s}", .{ browse_path, file_name }) catch "";
            if (EditorState.asset_db.findByPath(gp)) |info| break :blk info.guid.toString(&guid_buf);
            break :blk "";
        } else "";

        if (maybe_guid_str.len > 0) {
            if (gui.menuItemLabel(@src(), "Copy GUID", .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
                fw.close();
                gui.clipboardTextSet(maybe_guid_str);
            }
        }
    }
}
