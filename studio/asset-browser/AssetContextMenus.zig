//! Context-menu content shared by all three asset browser views (grid tiles,
//! folder tree, full tree): the cascaded "Create" asset-creation menu, and the per-asset
//! Open/Instantiate/Reimport/Reveal/Copy-path/Copy-GUID items. Each view
//! still draws its own Rename/Delete (the grid manages inline rename state
//! itself; `TreeView` provides Rename/Delete generically for tree rows) —
//! this file covers everything else, so the three views can't drift out of
//! sync with each other.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const AssetActions = @import("AssetActions.zig");
const Documents = @import("../main-window/Documents.zig");
const PreviewSystem = @import("preview/PreviewSystem.zig");
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

/// What a "Create" leaf entry does when picked. Kept as a closed union
/// (rather than a function pointer) because two variants need to carry data
/// (`preset`, `def`) that the caller only has as a runtime value, not
/// something a plain `*const fn(browse_path)` could capture.
const CreateAction = union(enum) {
    folder,
    prefab,
    project_settings,
    input_actions,
    ui_document,
    material_preset: engine.Material.Preset,
    data_asset: *const editor.ComponentDef,
};

fn runCreateAction(action: CreateAction, browse_path: []const u8) void {
    switch (action) {
        .folder => AssetActions.createNewFolder(browse_path),
        .prefab => AssetActions.createNewPrefab(browse_path),
        .project_settings => AssetActions.createNewProjectSettings(browse_path),
        .input_actions => AssetActions.createNewInputActions(browse_path),
        .ui_document => AssetActions.createNewUiDocument(browse_path),
        .material_preset => |preset| AssetActions.createNewMaterialFromPreset(browse_path, preset),
        .data_asset => |def| AssetActions.createNewDataAsset(browse_path, def),
    }
}

/// One entry in the "Create" cascaded menu: a runtime
/// `menu_path` string (e.g. `"Material/Metal"`) grouped into a tree by
/// `editor.menu_tree`, plus the action to run when picked.
const CreateEntry = struct {
    menu_path: []const u8,
    action: CreateAction,
};

/// Builtin entries plus the runtime-discovered material presets and
/// data-asset component types, each declaring its own cascaded `menu_path`.
/// This is the "registration" the issue asks for — adding a new builtin
/// creatable type, or a plugin contributing one later, is just another
/// entry with a path, no attribute/macro system required.
///
/// Paths aren't hardcoded here: each *type* owns its own path at the
/// declaration site closest to it —
///   - builtin file-backed types (Prefab, Settings, UI Document, Material's
///     category) declare `AssetDescriptor.create_menu_path` next to their
///     other editor metadata in `asset_registry.get()`.
///   - user data-asset types declare `pub const menu_path = "...";` on the
///     type itself (`Scanner.MENU_PATH_MARKER`), read into
///     `ComponentDef.menuPath()`.
/// `collectCreateEntries` just gathers what each type already declared
/// (falling back to `Data/<display name>` for data-asset types that don't
/// bother declaring one) — it doesn't own the taxonomy itself. "Folder" has
/// no `AssetType` (it's not a file extension), so it stays a literal here.
fn collectCreateEntries(alloc: std.mem.Allocator) []const CreateEntry {
    var entries: std.ArrayList(CreateEntry) = .empty;
    entries.append(alloc, .{ .menu_path = "Folder", .action = .folder }) catch {};

    const prefab_path = editor.asset_registry.get(.scene).create_menu_path orelse "Prefab";
    entries.append(alloc, .{ .menu_path = prefab_path, .action = .prefab }) catch {};
    const project_settings_path = editor.asset_registry.get(.project_settings).create_menu_path orelse "Project Settings";
    entries.append(alloc, .{ .menu_path = project_settings_path, .action = .project_settings }) catch {};
    const input_actions_path = editor.asset_registry.get(.input_actions).create_menu_path orelse "Input Actions";
    entries.append(alloc, .{ .menu_path = input_actions_path, .action = .input_actions }) catch {};
    const ui_document_path = editor.asset_registry.get(.ui_document).create_menu_path orelse "UI Document";
    entries.append(alloc, .{ .menu_path = ui_document_path, .action = .ui_document }) catch {};

    const material_category = editor.asset_registry.get(.material).create_menu_path orelse "Material";
    for (engine.Material.presets) |preset| {
        const path = std.fmt.allocPrint(alloc, "{s}/{s}", .{ material_category, preset.name }) catch continue;
        entries.append(alloc, .{ .menu_path = path, .action = .{ .material_preset = preset } }) catch {};
    }

    for (EditorState.discovered_components[0..EditorState.discovered_count]) |*def| {
        if (def.kind != .data_asset) continue;
        const declared = def.menuPath();
        const path = if (declared.len > 0)
            declared
        else
            std.fmt.allocPrint(alloc, "Data/{s}", .{def.displayName()}) catch continue;
        entries.append(alloc, .{ .menu_path = path, .action = .{ .data_asset = def } }) catch {};
    }

    return entries.items;
}

/// Recursively render one level of the cascaded "Create" menu: categories
/// (nodes with children) open a nested `floatingMenu`; leaves run their
/// action and close the whole chain via the outermost `fw`. Reusing the same
/// `@src()` at every recursion depth is safe — each level runs inside its
/// own freshly opened `floatingMenu`, which is what gives sibling and
/// cross-depth widgets distinct ids (the same pattern dvui's own recursive
/// `submenus()` example relies on).
fn drawMenuNode(node: *const editor.menu_tree.Node, fw: *gui.FloatingMenuWidget, entries: []const CreateEntry, browse_path: []const u8) void {
    for (node.children.items, 0..) |*child, i| {
        if (child.children.items.len > 0) {
            // Trailing "▸" (issue: submenus need a visual has-children signal,
            // same glyph `UiDocumentEditor`'s "Add Control ▸" already uses for
            // "this opens another menu").
            var cat_label_buf: [160]u8 = undefined;
            const cat_label = std.fmt.bufPrint(&cat_label_buf, "{s} \u{25b8}", .{child.name}) catch child.name;
            if (gui.menuItemLabel(@src(), cat_label, .{ .submenu = true }, .{ .expand = .horizontal, .id_extra = i })) |r| {
                var sub_fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
                defer sub_fw.deinit();
                drawMenuNode(child, sub_fw, entries, browse_path);
            }
        } else if (child.leaf) |leaf_idx| {
            var label_buf: [160]u8 = undefined;
            const label = std.fmt.bufPrint(&label_buf, "{s}", .{child.name}) catch child.name;
            if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = i }) != null) {
                fw.close();
                runCreateAction(entries[leaf_idx].action, browse_path);
            }
        }
    }
}

/// "Create" cascaded menu item, scoped to create inside
/// `browse_path`. Shared by the grid's empty-area menu, the folder tree's
/// per-folder menu, and the full tree's per-folder menu. `id_base` keeps
/// `id_extra` from colliding when a caller draws more than one of these in
/// the same menu. Closing the enclosing context menu on pick is handled by
/// `MenuWidget.close_chain` (any nested `floatingMenu.close()` walks up to
/// and closes the whole chain), so this doesn't need the caller's `fw`.
pub fn drawCreateAssetMenuItems(browse_path: []const u8, id_base: usize) void {
    const alloc = gui.currentWindow().arena();
    const entries = collectCreateEntries(alloc);

    var paths = alloc.alloc([]const u8, entries.len) catch return;
    for (entries, 0..) |e, i| paths[i] = e.menu_path;

    var root = editor.menu_tree.build(alloc, paths) catch return;
    defer root.deinit(alloc);

    if (gui.menuItemLabel(@src(), "Create \u{25b8}", .{ .submenu = true }, .{ .expand = .horizontal, .id_extra = id_base })) |r| {
        var sub_fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer sub_fw.deinit();
        drawMenuNode(&root, sub_fw, entries, browse_path);
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
