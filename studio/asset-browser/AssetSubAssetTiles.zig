//! Compound-model expansion (Unity-style): a model tile can be expanded to
//! reveal its generated sub-assets (materials/textures) as ordinary tiles
//! flowing inline in the *main* grid flexbox, right after the model's own
//! tile, rather than a separate boxed block that would break the grid flow.
//! Split out of `AssetGridView.zig` to keep that file under
//! the project's long-file budget — this half is a self-contained concern
//! `AssetGridView.draw`'s main loop only touches through `drawExpandToggle`/
//! `isExpanded`/`drawInlineSubAssets`.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const PreviewSystem = @import("preview/PreviewSystem.zig");
const AssetContextMenus = @import("AssetContextMenus.zig");
const AssetTileLayout = @import("AssetTileLayout.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

// Expand/collapse state for model tiles with sub-assets. Keyed by full asset
// path; not persisted (resets each session).
const MAX_EXPANDED = 64;
var expanded_paths: [MAX_EXPANDED][1024]u8 = undefined;
var expanded_lens: [MAX_EXPANDED]usize = [_]usize{0} ** MAX_EXPANDED;
var expanded_count: usize = 0;

pub fn isExpanded(path: []const u8) bool {
    for (0..expanded_count) |i| {
        if (std.mem.eql(u8, expanded_paths[i][0..expanded_lens[i]], path)) return true;
    }
    return false;
}

fn toggleExpanded(path: []const u8) void {
    for (0..expanded_count) |i| {
        if (std.mem.eql(u8, expanded_paths[i][0..expanded_lens[i]], path)) {
            expanded_paths[i] = expanded_paths[expanded_count - 1];
            expanded_lens[i] = expanded_lens[expanded_count - 1];
            expanded_count -= 1;
            return;
        }
    }
    if (expanded_count >= MAX_EXPANDED) return;
    const n = @min(path.len, expanded_paths[expanded_count].len);
    @memcpy(expanded_paths[expanded_count][0..n], path[0..n]);
    expanded_lens[expanded_count] = n;
    expanded_count += 1;
}

/// Small round chevron toggle drawn at a model tile's right edge, shown only
/// when the model actually produced sub-assets on import. Toggles whether its
/// sub-asset group box is revealed to the right.
pub fn drawExpandToggle(asset_path: []const u8, id_extra: usize) void {
    const count = AssetContextMenus.cachedSubAssetCount(asset_path) orelse blk: {
        var meta_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer meta_arena.deinit();
        const meta = editor.asset_meta.readMeta(gui.io, meta_arena.allocator(), asset_path);
        AssetContextMenus.setSubAssetCount(asset_path, meta.sub_assets.len);
        break :blk meta.sub_assets.len;
    };
    if (count == 0) return;

    const expanded = isExpanded(asset_path);
    // Small round toggle sitting at the model tile's right edge, vertically
    // centered — a chevron that points right (toward where the sub-assets
    // appear) when collapsed, left (collapse) when expanded.
    if (gui.buttonIcon(
        @src(),
        "expand_toggle",
        if (expanded) gui.entypo.circle_with_minus else gui.entypo.circle_with_plus,
        .{},
        .{},
        .{
            .gravity_x = 0.5,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 16, .h = 16 },
            .padding = .all(4),
            .margin = .all(2),
            .corners = gui.CornerRect.all(999),
            .id_extra = id_extra,
        },
    )) {
        toggleExpanded(asset_path);
    }
}

/// Draw an expanded model's generated sub-assets (materials/textures) as tiles
/// flowing inline in the *main* grid flexbox, right after the model's own tile
/// (Unity-style), rather than a separate boxed block that would break the grid
/// flow. They are ordinary tiles — same size, same wrapping — just drawn on a
/// distinct background (see `drawSubAssetTile`) so the run of them reads as a
/// group belonging to the model, continuing onto the next line when it wraps.
/// Called from the main tile loop after the model tile closes.
pub fn drawInlineSubAssets(proj_path: []const u8, model_path: []const u8, entry_idx: usize) void {
    var meta_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer meta_arena.deinit();
    const meta = editor.asset_meta.readMeta(gui.io, meta_arena.allocator(), model_path);

    for (meta.sub_assets, 0..) |sub, si| {
        // id_extra kept clear of the main tiles' `entry_idx` range and the
        // up-tile (99999) so sub-asset tile ids never collide with them.
        drawSubAssetTile(proj_path, sub, 2_000_000 + entry_idx * 100 + si);
    }
}

/// A single generated sub-asset (material/texture/… from a model's import) as
/// a tile: previewed via `PreviewSystem.imageSourceForGuid`, click selects it
/// in the Inspector — the same navigation `Inspector.drawSubAssets`'s button
/// list already offers, just with a thumbnail and inline in the browser.
fn drawSubAssetTile(proj_path: []const u8, sub: editor.SubAsset, id_extra: usize) void {
    var path_buf: [1024]u8 = undefined;
    const cache_path = editor.asset_cache.artifactPath(proj_path, sub.guid, sub.asset_type, &path_buf) orelse return;

    var guid_buf: [36]u8 = undefined;
    const guid_str = sub.guid.toString(&guid_buf);

    const is_selected = if (EditorState.selected_asset_path) |sel| std.mem.eql(u8, sel, cache_path) else false;

    // Same footprint as a regular tile so it flows/wraps identically in the
    // grid, but drawn on the `.window` fill (vs regular tiles' `.content`) and
    // butted together horizontally (zero x-margin) so a run of sub-assets reads
    // as one continuous background band that carries onto the next line when it
    // wraps — the inline-"span" grouping, not a separate block.
    var tile = gui.box(@src(), .{}, .{
        .id_extra = id_extra,
        .min_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
        .max_size_content = .{ .w = AssetTileLayout.tileWidth(), .h = AssetTileLayout.tileHeight() },
        .background = true,
        .style = if (is_selected) .highlight else .window,
        .border = .all(if (is_selected) 2 else 0),
        .corners = .all(0),
        .margin = .{ .x = 0, .y = 2, .w = 0, .h = 2 },
        .padding = .all(4),
        .gravity_x = 0.5,
    });
    defer tile.deinit();

    // "Instantiate into Scene" for a hierarchy sub-asset (`asset_type ==
    // .scene`, e.g. a glTF import's node-graph prefab — see
    // `ModelHierarchy.zig`). `instantiatePrefab` resolves by the exact
    // registered path, which for a sub-asset is this cache artifact path
    // (`AssetDatabase.registerDerived`), so no browse_path/file_name
    // reconstruction is needed here unlike the top-level tile's version.
    if (sub.asset_type == .scene) {
        const cxt = gui.context(@src(), .{ .rect = tile.data().borderRectScale().r }, .{ .id_extra = id_extra });
        defer cxt.deinit();

        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{ .id_extra = id_extra });
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), tr("Instantiate into Scene"), .{}, .{ .expand = .horizontal, .id_extra = id_extra }) != null) {
                fw.close();
                _ = EditorState.instantiatePrefab(gui.frameTimeNS(), gui.io, cache_path);
            }
        }
    }

    for (gui.events()) |*e| {
        if (!gui.eventMatchSimple(e, tile.data())) continue;
        if (e.evt == .mouse) {
            const me = e.evt.mouse;
            if (me.action == .press and me.button == .left) {
                e.handle(@src(), tile.data());
                EditorState.selectAsset(cache_path);
                EditorState.startDragAsset(cache_path);
            }
        }
    }

    const desc = editor.asset_registry.get(sub.asset_type);
    if (PreviewSystem.imageSourceForGuid(guid_str, cache_path, sub.asset_type)) |source| {
        _ = gui.image(@src(), .{ .source = source, .shrink = .ratio }, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = AssetTileLayout.tile_content, .h = AssetTileLayout.tile_content },
            .id_extra = id_extra,
        });
    } else {
        gui.icon(@src(), "sub_tile_icon", AssetContextMenus.iconForHint(desc.icon_hint), .{}, .{
            .gravity_x = 0.5,
            .min_size_content = .{ .w = AssetTileLayout.tile_content, .h = AssetTileLayout.tile_content },
            .id_extra = id_extra,
        });
    }

    var name_buf: [300]u8 = undefined;
    const display_name = AssetTileLayout.truncatedDisplayName(sub.name, false, AssetTileLayout.tileContentWidth(), &name_buf);
    gui.label(@src(), "{s}", .{display_name orelse sub.name}, .{
        .gravity_x = 0.5,
        .id_extra = id_extra,
    });
    if (display_name != null) {
        gui.tooltip(@src(), .{ .active_rect = tile.data().rectScale().r }, "{s}", .{sub.name}, .{ .id_extra = id_extra });
    }
}
