//! Registry of dockable panels (#90): one entry per panel slug, each with a
//! draw function taking over context-aware dispatch that used to be
//! hardcoded in Window.zig (uidoc/settings tabs swapping what Hierarchy and
//! Scene show; dedicated asset editors taking over Scene).
const std = @import("std");
const gui = @import("gui");
const SceneTree = @import("../scene-hierarchy/SceneTree.zig");
const Inspector = @import("../inspector/Inspector.zig");
const AssetBrowser = @import("../asset-browser/AssetBrowser.zig");
const SceneViewport = @import("../scene-view/SceneViewport.zig");
const Documents = @import("Documents.zig");
const ProfilerPanel = @import("ProfilerPanel.zig");
const UiDocumentEditor = @import("../inspector/editor/UiDocumentEditor.zig");
const SettingsEditor = @import("../inspector/editor/SettingsEditor.zig");

pub const PanelDesc = struct {
    id: []const u8,
    title: []const u8,
    icon: []const u8,
    draw: *const fn () void,
    closable: bool = true,
};

pub const all = [_]PanelDesc{
    .{ .id = "hierarchy", .title = "Hierarchy", .icon = gui.entypo.flow_tree, .draw = drawHierarchy, .closable = false },
    .{ .id = "scene", .title = "Scene", .icon = gui.entypo.image, .draw = drawScene, .closable = false },
    .{ .id = "inspector", .title = "Inspector", .icon = gui.entypo.list, .draw = Inspector.draw },
    .{ .id = "assets", .title = "Assets", .icon = gui.entypo.folder, .draw = AssetBrowser.draw },
    .{ .id = "profiler", .title = "Profiler", .icon = gui.entypo.gauge, .draw = ProfilerPanel.drawContent },
};

pub fn find(id: []const u8) ?*const PanelDesc {
    for (&all) |*p| {
        if (std.mem.eql(u8, p.id, id)) return p;
    }
    return null;
}

/// Draws `id`'s content, or nothing if `id` isn't a known panel (e.g. a
/// stale slug loaded from an old layout.json).
pub fn drawById(id: []const u8) void {
    if (find(id)) |p| p.draw();
}

/// Hierarchy panel: the scene tree, except a `.studio_settings` or
/// `.ui_document` tab takes it over with its own sidebar/hierarchy.
fn drawHierarchy() void {
    const is_settings = Documents.activeIsAsset() and Documents.activeAssetType() == .studio_settings;
    const is_uidoc = Documents.activeIsAsset() and Documents.activeAssetType() == .ui_document;

    if (is_settings) {
        SettingsEditor.drawSidebar();
    } else if (is_uidoc) {
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
    const is_settings = Documents.activeIsAsset() and Documents.activeAssetType() == .studio_settings;
    const is_uidoc = Documents.activeIsAsset() and Documents.activeAssetType() == .ui_document;
    const is_other_asset = Documents.activeIsAsset() and !is_settings and !is_uidoc;

    if (is_other_asset) {
        Inspector.drawAssetDocument(Documents.activePath());
    } else if (is_uidoc) {
        UiDocumentEditor.drawViewPanel(Documents.activePath());
    } else {
        SceneViewport.draw();
    }
}
