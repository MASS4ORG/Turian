//! Persists the dockable panel layout (#90) as `layout.json` next to
//! `editor.Settings`'s global settings file (`<global_dir>/.turian/`) — a
//! window arrangement is a per-machine preference, not per-project data.
//!
//! `dvui.DockLayout.parseJson`/`writeJson` do the actual (de)serialization;
//! this module only owns the single live instance, its file path, and
//! reconciling a loaded file against the current `Panels.all` registry
//! (unknown slugs dropped, non-closable panels re-added if missing).
const std = @import("std");
const gui = @import("gui");
const Panels = @import("../main-window/Panels.zig");

pub const DockLayout = gui.DockingWidget.Layout.DockLayout;

const SETTINGS_DIR = ".turian"; // matches editor.Settings' SETTINGS_DIR
const LAYOUT_FILE = "layout.json";

var g_layout: ?DockLayout = null;
var g_path: ?[]u8 = null;
var g_allocator: std.mem.Allocator = undefined;

/// Loads the saved layout (or builds the default), and remembers `allocator`
/// for `save`/`reset`. Never fails: a missing/corrupt file falls back to the
/// default layout instead of erroring.
pub fn init(allocator: std.mem.Allocator, io: std.Io, global_dir: []const u8) void {
    g_allocator = allocator;
    const sep = std.fs.path.sep_str;
    g_path = std.fmt.allocPrint(allocator, "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ LAYOUT_FILE, .{global_dir}) catch null;
    g_layout = loadFromDisk(allocator, io) orelse (buildDefaultLayout(allocator) catch @panic("OOM building default dock layout"));
}

pub fn deinit(io: std.Io) void {
    if (g_layout != null) {
        save(io);
        g_layout.?.deinit();
        g_layout = null;
    }
    if (g_path) |p| {
        g_allocator.free(p);
        g_path = null;
    }
}

/// The live layout instance — pass to `dvui.dockspace`.
pub fn get() *DockLayout {
    return &g_layout.?;
}

/// Serializes and writes the current layout to disk. Logs and gives up on
/// failure (I/O errors here shouldn't take down the editor).
pub fn save(io: std.Io) void {
    const path = g_path orelse return;
    const l = if (g_layout) |*l| l else return;

    var out: std.Io.Writer.Allocating = .init(g_allocator);
    defer out.deinit();
    l.writeJson(&out.writer) catch |err| {
        std.log.warn("LayoutStore: failed to serialize layout: {t}", .{err});
        return;
    };

    ensureParentDir(io, path);
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.written() }) catch |err| {
        std.log.warn("LayoutStore: failed to write {s}: {t}", .{ path, err });
    };
}

/// Discards the current layout and rebuilds + saves the default arrangement.
pub fn reset(io: std.Io) void {
    if (g_layout) |*l| l.deinit();
    g_layout = buildDefaultLayout(g_allocator) catch @panic("OOM building default dock layout");
    save(io);
}

fn loadFromDisk(allocator: std.mem.Allocator, io: std.Io) ?DockLayout {
    const path = g_path orelse return null;
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return null;
    defer allocator.free(content);

    var l = DockLayout.parseJson(allocator, content) catch |err| {
        std.log.warn("LayoutStore: failed to parse {s} ({t}), using default layout", .{ path, err });
        return null;
    };
    reconcile(&l, allocator) catch {
        l.deinit();
        return null;
    };
    return l;
}

/// Drops tabs whose slug isn't a known panel (e.g. from an older/newer
/// Studio version), and re-adds any non-closable panel that's missing
/// entirely (those are always supposed to be present somewhere).
fn reconcile(l: *DockLayout, allocator: std.mem.Allocator) !void {
    var to_drop: std.ArrayList([]const u8) = .empty;
    defer to_drop.deinit(allocator);
    for (l.nodes.items) |n| {
        switch (n) {
            .leaf => |leaf| for (leaf.tabs.items) |slug| {
                if (Panels.find(slug) == null) try to_drop.append(allocator, slug);
            },
            .split, .free => {},
        }
    }
    for (to_drop.items) |slug| l.removePanel(slug);

    for (Panels.all) |p| {
        if (p.closable or l.contains(p.id)) continue;
        try l.insertTabOwned(l.firstLeaf(l.root), 0, p.id);
    }
}

fn ensureParentDir(io: std.Io, path: []const u8) void {
    const dir = std.fs.path.dirname(path) orelse return;
    std.Io.Dir.cwd().createDirPath(io, dir) catch {};
}

/// Matches the previous fixed layout's arrangement: [hierarchy | scene] over
/// assets, on the left; inspector on the right.
fn buildDefaultLayout(allocator: std.mem.Allocator) !DockLayout {
    var l = try DockLayout.initSingleLeaf(allocator, "hierarchy");
    try l.splitLeaf(l.root, .right, "scene");
    l.nodes.items[l.root].split.ratio = 0.28;

    try l.splitRoot(.bottom, "assets");
    l.nodes.items[l.root].split.ratio = 0.75;

    try l.splitRoot(.right, "inspector");
    l.nodes.items[l.root].split.ratio = 0.7;

    return l;
}
