//! Registry of dockable panels: one entry per panel slug, each with a draw
//! function taking over context-aware dispatch that used to be hardcoded in
//! Window.zig (uidoc/settings tabs swapping what Hierarchy and Scene show;
//! dedicated asset editors taking over Scene).
//!
//! `all()` is a runtime registry (builtins + `registerCustom`), not a
//! comptime list, so it can be re-scanned after user code compiles. Studio
//! itself is a plain native binary, though — unlike Play mode, which
//! dlopen()s a hot-compiled library through a C ABI, it has no mechanism to
//! execute arbitrary third-party dvui-drawing code. So `registerCustom` is
//! wired up (called after every `ReflectJob` rescan) but nothing feeds it
//! real descriptors yet; that needs either reflection-driven generic panel
//! content or a native dlopen'd Studio-side plugin.
const std = @import("std");
const gui = @import("gui");
const SceneTree = @import("../scene-hierarchy/SceneTree.zig");
const Inspector = @import("../inspector/Inspector.zig");
const AssetBrowser = @import("../asset-browser/AssetBrowser.zig");
const SceneViewport = @import("../scene-view/SceneViewport.zig");
const Documents = @import("Documents.zig");
const ProfilerPanel = @import("ProfilerPanel.zig");
const LogPanel = @import("LogPanel.zig");
const UiDocumentEditor = @import("../inspector/editor/UiDocumentEditor.zig");
const SettingsEditor = @import("../inspector/editor/SettingsEditor.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

pub const PanelDesc = struct {
    id: []const u8,
    title: []const u8,
    icon: []const u8,
    draw: *const fn () void,
    closable: bool = true,
    /// Draws the body of the dock header's "..." menu for this panel
    /// (checkboxes, sliders, whatever) — null means the panel has nothing
    /// to configure there, so no button is shown at all. Takes the instance
    /// id (e.g. "inspector" or "inspector#2") in case a panel wants
    /// per-instance settings; most panels ignore it.
    settings: ?*const fn (instance_id: []const u8) void = null,
    /// Whether more than one dock tab of this panel type can be open at
    /// once (default: yes). Panels tied to unique global state (the scene
    /// tree, the 3D viewport) set this false.
    allow_multiple: bool = true,
};

/// Hierarchy and Scene allow multiple instances: Hierarchy's state
/// (selection, object list) is already global/shared, so extra copies are
/// harmless; Scene gets its own per-instance camera (see
/// `SceneViewport.InstanceState`) so multiple copies can show the scene
/// from different angles at once. Game stays single-instance: it shows the
/// one running simulation's own camera output, which a second copy
/// couldn't vary.
const builtin_panels = [_]PanelDesc{
    .{ .id = "hierarchy", .title = "Hierarchy", .icon = gui.entypo.flow_tree, .draw = drawHierarchy, .closable = false },
    .{ .id = "scene", .title = "Scene", .icon = gui.entypo.image, .draw = drawScene, .closable = false },
    .{ .id = "game", .title = "Game", .icon = gui.entypo.game_controller, .draw = SceneViewport.drawGame, .allow_multiple = false },
    .{ .id = "inspector", .title = "Inspector", .icon = gui.entypo.list, .draw = Inspector.draw },
    .{ .id = "assets", .title = "Assets", .icon = gui.entypo.folder, .draw = AssetBrowser.draw, .settings = AssetBrowser.drawSettings },
    .{ .id = "profiler", .title = "Profiler", .icon = gui.entypo.gauge, .draw = ProfilerPanel.drawContent },
    .{ .id = "output", .title = "Log", .icon = gui.entypo.text_document, .draw = LogPanel.draw, .settings = LogPanel.drawSettings },
    .{ .id = "settings", .title = "Settings", .icon = gui.entypo.cog, .draw = SettingsEditor.drawSidebar, .allow_multiple = false },
};

/// The registry backing `all()`: builtins first (fixed), then whatever
/// `registerCustom` last supplied. Grown lazily since module-scope `var`s
/// can't run init code; lives for the process lifetime (no `deinit`, same
/// as every other Studio singleton service).
var g_registry: std.ArrayList(PanelDesc) = .empty;
var g_registry_inited = false;

fn ensureInit() void {
    if (g_registry_inited) return;
    g_registry_inited = true;
    g_registry.appendSlice(std.heap.page_allocator, &builtin_panels) catch {};
}

/// Every registered panel: the fixed builtins plus any custom ones from the
/// last `registerCustom` call.
pub fn all() []const PanelDesc {
    ensureInit();
    return g_registry.items;
}

/// Custom-panel extension point: replaces whatever was previously
/// registered after the builtins with `descs`. Called after a user-code
/// discovery rescan completes (see `ReflectJob.finishReflect`) so the panel
/// list stays in sync with what's actually compiled. Nothing populates
/// this with real descriptors yet — see this file's module doc for why.
pub fn registerCustom(descs: []const PanelDesc) void {
    ensureInit();
    g_registry.shrinkRetainingCapacity(builtin_panels.len);
    g_registry.appendSlice(std.heap.page_allocator, descs) catch {};
}

/// Localized display title for a panel. Builtin titles are translated by id
/// (the `PanelDesc.title` field itself stays English, since `tr()` needs a
/// comptime string and the registry is a runtime-built list); any other
/// (custom) panel falls back to its own `title` field untranslated.
pub fn translatedTitle(p: *const PanelDesc) []const u8 {
    if (std.mem.eql(u8, p.id, "hierarchy")) return tr("Hierarchy");
    if (std.mem.eql(u8, p.id, "scene")) return tr("Scene");
    if (std.mem.eql(u8, p.id, "game")) return tr("Game");
    if (std.mem.eql(u8, p.id, "inspector")) return tr("Inspector");
    if (std.mem.eql(u8, p.id, "assets")) return tr("Assets");
    if (std.mem.eql(u8, p.id, "profiler")) return tr("Profiler");
    if (std.mem.eql(u8, p.id, "output")) return tr("Log");
    if (std.mem.eql(u8, p.id, "settings")) return tr("Settings");
    return p.title;
}

/// Strips a `"#N"` instance suffix (see `newInstanceId`) to get back the
/// registry id a dock tab's slug was generated from.
pub fn baseId(instance_id: []const u8) []const u8 {
    if (std.mem.lastIndexOfScalar(u8, instance_id, '#')) |i| return instance_id[0..i];
    return instance_id;
}

/// The `N` in a `"#N"` instance suffix, or null for the bare (first)
/// instance — so `panelInfo` can label extra copies "Inspector (2)" etc.
pub fn instanceNumber(instance_id: []const u8) ?u32 {
    const i = std.mem.lastIndexOfScalar(u8, instance_id, '#') orelse return null;
    return std.fmt.parseInt(u32, instance_id[i + 1 ..], 10) catch null;
}

pub fn find(instance_id: []const u8) ?*const PanelDesc {
    const base = baseId(instance_id);
    for (all()) |*p| {
        if (std.mem.eql(u8, p.id, base)) return p;
    }
    return null;
}

/// Draws `id`'s content, or nothing if `id` isn't a known panel (e.g. a
/// stale slug loaded from an old layout.json).
pub fn drawById(id: []const u8) void {
    if (find(id)) |p| p.draw();
}

/// A fresh, layout-unique instance id for `base_id`: the bare id itself if
/// no tab of this panel is open yet, else the lowest unused `"#N"` suffix
/// (2, 3, ...). Returned string is duped with `allocator` — callers hand it
/// to `DockLayout.insertTabOwned`, which expects an id it can own.
pub fn newInstanceId(base_id: []const u8, layout: *gui.DockingWidget.Layout.DockLayout, allocator: std.mem.Allocator) ![]const u8 {
    if (!layout.contains(base_id)) return try allocator.dupe(u8, base_id);
    var n: u32 = 2;
    var buf: [128]u8 = undefined;
    while (true) : (n += 1) {
        const candidate = try std.fmt.bufPrint(&buf, "{s}#{d}", .{ base_id, n });
        if (!layout.contains(candidate)) return try allocator.dupe(u8, candidate);
    }
}

/// Draws "Add <Panel>" menu items — every registered panel that `allows` for
/// the layout on screen and that either permits more than one open copy, or
/// isn't open yet — inserting a fresh instance into `target_leaf` on click.
/// Shared by the View ▸ menu (target = the first leaf) and the dock header's
/// right-click context menu (target = whichever leaf was clicked). Returns
/// true if an item was picked (already applied to `l`), so the caller knows
/// to close its menu and persist — this function doesn't know or care what
/// kind of menu/popup it's drawn inside.
///
/// `allows` is passed in rather than queried here because the answer depends
/// on which layout is live, which is `LayoutStore`'s business — and
/// `LayoutStore` already imports this module.
pub fn drawAddPanelMenuItems(
    l: *gui.DockingWidget.Layout.DockLayout,
    target_leaf: gui.DockingWidget.Layout.NodeIndex,
    allows: *const fn (id: []const u8) bool,
) bool {
    var picked = false;
    for (all(), 0..) |p, i| {
        if (!allows(p.id)) continue;
        if (!p.allow_multiple and l.contains(p.id)) continue;
        const label = StudioLocale.trArgs("Add {title}", &.{.{ .name = "title", .value = .{ .text = translatedTitle(&p) } }});
        if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = i }) != null) {
            const id = newInstanceId(p.id, l, std.heap.page_allocator) catch null;
            if (id) |instance_id| {
                l.insertTabOwned(target_leaf, 0, instance_id) catch {};
                picked = true;
            }
        }
    }
    return picked;
}

/// Hierarchy panel: the scene tree, except a `.ui_document` tab takes it over
/// with its own hierarchy. A `.studio_settings` tab needs no special case —
/// it swaps to a layout of its own (`LayoutPresets.forAssetType`) in which
/// the dedicated `settings` panel stands in for this one.
fn drawHierarchy() void {
    if (Documents.activeIsAsset() and Documents.activeAssetType() == .ui_document) {
        UiDocumentEditor.drawHierarchyPanel(Documents.activePath());
    } else {
        SceneTree.draw();
    }
}

/// Scene/viewport panel: the 3D viewport, except a `.ui_document` tab shows
/// its view panel instead, and any *other* asset tab hosts its dedicated
/// editor here (Hierarchy keeps showing the scene tree in that case — the
/// old fixed-layout special case where the asset editor took over both the
/// hierarchy and scene area is gone; this is an acceptable simplification).
fn drawScene() void {
    const is_uidoc = Documents.activeIsAsset() and Documents.activeAssetType() == .ui_document;
    const is_other_asset = Documents.activeIsAsset() and !is_uidoc;

    if (is_other_asset) {
        Inspector.drawAssetDocument(Documents.activePath());
    } else if (is_uidoc) {
        UiDocumentEditor.drawViewPanel(Documents.activePath());
    } else {
        SceneViewport.draw();
    }
}
