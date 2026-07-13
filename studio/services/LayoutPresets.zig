//! Built-in layout presets, Unity-style: alternate dock arrangements of
//! the same real panels this build actually has. Not literal recreations of
//! Unity's intra-panel multi-camera "4 Split" (a much bigger feature this
//! repo doesn't have) — just different splits/groupings of
//! hierarchy/scene+game/assets/inspector. `LayoutStore` owns applying one of
//! these (and saving/loading user-defined presets); this file only builds
//! layout trees.
const std = @import("std");
const gui = @import("gui");

pub const DockLayout = gui.DockingWidget.Layout.DockLayout;

pub const Preset = struct {
    name: []const u8,
    build: *const fn (allocator: std.mem.Allocator) anyerror!DockLayout,
};

pub const builtins = [_]Preset{
    .{ .name = "Default", .build = buildDefault },
    .{ .name = "4 Split", .build = build4Split },
    .{ .name = "2x3", .build = build2x3 },
    .{ .name = "Tall", .build = buildTall },
    .{ .name = "Wide", .build = buildWide },
};

/// Groups "scene" and "game" as tabs in `leaf`, with Scene active (Unity's
/// default — the just-inserted tab would otherwise become active).
fn addGameTab(l: *DockLayout, leaf: DockLayout.NodeIndex) !void {
    try l.insertTab(leaf, 1, "game");
    l.nodes.items[leaf].leaf.active = 0;
}

/// [hierarchy | scene+game] over assets, on the left; inspector on the right.
pub fn buildDefault(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "hierarchy");
    try l.splitLeaf(l.root, .right, "scene");
    l.nodes.items[l.root].split.ratio = 0.28;
    try addGameTab(&l, l.findPanel("scene").?);

    try l.splitRoot(.bottom, "assets");
    l.nodes.items[l.root].split.ratio = 0.75;

    try l.splitRoot(.right, "inspector");
    l.nodes.items[l.root].split.ratio = 0.7;

    return l;
}

/// 2x2 quadrants: hierarchy | scene+game on top, assets | inspector below.
fn build4Split(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "hierarchy");
    try l.splitRoot(.bottom, "assets");
    l.nodes.items[l.root].split.ratio = 0.6;

    const top_leaf = l.findPanel("hierarchy").?;
    try l.splitLeaf(top_leaf, .right, "scene");
    l.nodes.items[top_leaf].split.ratio = 0.25;
    try addGameTab(&l, l.findPanel("scene").?);

    const bottom_leaf = l.findPanel("assets").?;
    try l.splitLeaf(bottom_leaf, .right, "inspector");
    l.nodes.items[bottom_leaf].split.ratio = 0.5;

    return l;
}

/// Two rows: a tall top row split into hierarchy | scene+game | inspector
/// (three columns), a short bottom row for assets.
fn build2x3(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "hierarchy");
    try l.splitRoot(.bottom, "assets");
    l.nodes.items[l.root].split.ratio = 0.78;

    const top_leaf = l.findPanel("hierarchy").?;
    try l.splitLeaf(top_leaf, .right, "scene");
    l.nodes.items[top_leaf].split.ratio = 0.22;
    const scene_leaf = l.findPanel("scene").?;
    try addGameTab(&l, scene_leaf);

    try l.splitLeaf(scene_leaf, .right, "inspector");
    l.nodes.items[scene_leaf].split.ratio = 0.75;

    return l;
}

/// A tall viewport spanning the top, hierarchy/assets split below it, and a
/// narrow inspector column running the full height on the right.
fn buildTall(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "scene");
    try addGameTab(&l, l.root);

    try l.splitRoot(.bottom, "hierarchy");
    l.nodes.items[l.root].split.ratio = 0.65;

    const bottom_leaf = l.findPanel("hierarchy").?;
    try l.splitLeaf(bottom_leaf, .right, "assets");
    l.nodes.items[bottom_leaf].split.ratio = 0.5;

    try l.splitRoot(.right, "inspector");
    l.nodes.items[l.root].split.ratio = 0.78;

    return l;
}

/// A wide viewport spanning the left, with hierarchy/assets/inspector
/// stacked in a narrow column on the right.
fn buildWide(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "scene");
    try addGameTab(&l, l.root);

    try l.splitRoot(.right, "hierarchy");
    l.nodes.items[l.root].split.ratio = 0.62;

    const right_leaf = l.findPanel("hierarchy").?;
    try l.splitLeaf(right_leaf, .bottom, "assets");
    l.nodes.items[right_leaf].split.ratio = 0.35;

    const assets_leaf = l.findPanel("assets").?;
    try l.splitLeaf(assets_leaf, .bottom, "inspector");
    l.nodes.items[assets_leaf].split.ratio = 0.5;

    return l;
}
