//! Persists the dockable panel layout (#90) as `layout.json` next to
//! `editor.Settings`'s global settings file (`<global_dir>/.turian/`) — a
//! window arrangement is a per-machine preference, not per-project data.
//! Also owns user-saved layout *presets* as
//! `<global_dir>/.turian/layouts/<name>.json`, alongside the built-in ones
//! in `LayoutPresets.zig`.
//!
//! `dvui.DockLayout.parseJson`/`writeJson` do the actual (de)serialization;
//! this module only owns the single live instance, its file path, and
//! reconciling a loaded file against the current `Panels.all` registry
//! (unknown slugs dropped, non-closable panels re-added if missing).
const std = @import("std");
const gui = @import("gui");
const Panels = @import("../main-window/Panels.zig");
const LayoutPresets = @import("LayoutPresets.zig");

pub const DockLayout = gui.DockingWidget.Layout.DockLayout;

const SETTINGS_DIR = ".turian"; // matches editor.Settings' SETTINGS_DIR
const LAYOUT_FILE = "layout.json";
const PRESETS_DIR = "layouts"; // under SETTINGS_DIR

var g_layout: ?DockLayout = null;
var g_path: ?[]u8 = null;
var g_presets_dir: ?[]u8 = null;
var g_allocator: std.mem.Allocator = undefined;

/// Loads the saved layout (or builds the default), and remembers `allocator`
/// for `save`/`reset`. Never fails: a missing/corrupt file falls back to the
/// default layout instead of erroring.
pub fn init(allocator: std.mem.Allocator, io: std.Io, global_dir: []const u8) void {
    g_allocator = allocator;
    const sep = std.fs.path.sep_str;
    g_path = std.fmt.allocPrint(allocator, "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ LAYOUT_FILE, .{global_dir}) catch null;
    g_presets_dir = std.fmt.allocPrint(allocator, "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ PRESETS_DIR, .{global_dir}) catch null;
    g_layout = loadFromDisk(allocator, io) orelse (LayoutPresets.buildDefault(allocator) catch @panic("OOM building default dock layout"));
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
    if (g_presets_dir) |p| {
        g_allocator.free(p);
        g_presets_dir = null;
    }
}

/// The live layout instance — pass to `dvui.dockspace`.
pub fn get() *DockLayout {
    return &g_layout.?;
}

/// Discards the current layout and replaces it with `new_layout` (already
/// built/loaded by the caller — a preset, typically), saving it as the new
/// `layout.json`. Takes ownership of `new_layout`.
pub fn replace(new_layout: DockLayout, io: std.Io) void {
    if (g_layout) |*l| l.deinit();
    g_layout = new_layout;
    save(io);
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
    g_layout = LayoutPresets.buildDefault(g_allocator) catch @panic("OOM building default dock layout");
    save(io);
}

fn loadFromDisk(allocator: std.mem.Allocator, io: std.Io) ?DockLayout {
    const path = g_path orelse return null;
    return loadFromPath(allocator, io, path);
}

fn loadFromPath(allocator: std.mem.Allocator, io: std.Io, path: []const u8) ?DockLayout {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return null;
    defer allocator.free(content);

    var l = DockLayout.parseJson(allocator, content) catch |err| {
        std.log.warn("LayoutStore: failed to parse {s} ({t})", .{ path, err });
        return null;
    };
    reconcile(&l, allocator) catch {
        l.deinit();
        return null;
    };
    return l;
}

/// Names (without `.json`) of every user-saved preset in `.turian/layouts/`,
/// sorted, allocated with `allocator`. Empty if the directory doesn't exist
/// yet (no preset saved) or can't be read.
pub fn listPresetNames(allocator: std.mem.Allocator, io: std.Io) [][]const u8 {
    var names: std.ArrayList([]const u8) = .empty;
    // `names.items` is already a correctly-typed (mutable, zero-length)
    // `[][]const u8` — used as the empty-result fallback below instead of
    // `&.{}`, which coerces to a `[]const []const u8` here and doesn't unify
    // with `std.mem.sort`'s `[]T` parameter.
    const dir_path = g_presets_dir orelse return names.items;

    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return names.items;
    defer dir.close(io);

    var it = dir.iterate();
    while (it.next(io) catch null) |entry| {
        if (entry.kind != .file) continue;
        const ext = ".json";
        if (!std.mem.endsWith(u8, entry.name, ext)) continue;
        const name = entry.name[0 .. entry.name.len - ext.len];
        names.append(allocator, allocator.dupe(u8, name) catch continue) catch continue;
    }
    const owned = names.toOwnedSlice(allocator) catch names.items;
    std.mem.sort([]const u8, owned, {}, struct {
        fn lessThan(_: void, a: []const u8, b: []const u8) bool {
            return std.mem.lessThan(u8, a, b);
        }
    }.lessThan);
    return owned;
}

/// Writes the *current* live layout to `.turian/layouts/<name>.json`,
/// overwriting any existing preset with that name.
pub fn savePreset(name: []const u8, io: std.Io) void {
    const dir_path = g_presets_dir orelse return;
    const l = if (g_layout) |*l| l else return;

    var out: std.Io.Writer.Allocating = .init(g_allocator);
    defer out.deinit();
    l.writeJson(&out.writer) catch |err| {
        std.log.warn("LayoutStore: failed to serialize preset '{s}': {t}", .{ name, err });
        return;
    };

    std.Io.Dir.cwd().createDirPath(io, dir_path) catch {};
    const path = std.fmt.allocPrint(g_allocator, "{s}" ++ std.fs.path.sep_str ++ "{s}.json", .{ dir_path, name }) catch return;
    defer g_allocator.free(path);
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.written() }) catch |err| {
        std.log.warn("LayoutStore: failed to write preset {s}: {t}", .{ path, err });
    };
}

/// Loads a previously-saved preset by name, reconciled against the current
/// panel registry like the main `layout.json`. Null if missing/corrupt.
pub fn loadPreset(name: []const u8, allocator: std.mem.Allocator, io: std.Io) ?DockLayout {
    const dir_path = g_presets_dir orelse return null;
    const path = std.fmt.allocPrint(allocator, "{s}" ++ std.fs.path.sep_str ++ "{s}.json", .{ dir_path, name }) catch return null;
    defer allocator.free(path);
    return loadFromPath(allocator, io, path);
}

/// A name not already used by any saved preset: `"Custom Layout"`, or
/// `"Custom Layout N"` for the lowest unused N — same auto-naming idiom as
/// `AssetActions.uniqueDirPath` ("New Folder", "New Folder 2", ...).
pub fn uniquePresetName(allocator: std.mem.Allocator, io: std.Io) []const u8 {
    const existing = listPresetNames(allocator, io);
    defer allocator.free(existing);
    defer for (existing) |n| allocator.free(n);

    if (!containsName(existing, "Custom Layout")) return allocator.dupe(u8, "Custom Layout") catch "Custom Layout";
    var n: u32 = 2;
    var buf: [64]u8 = undefined;
    while (true) : (n += 1) {
        const candidate = std.fmt.bufPrint(&buf, "Custom Layout {d}", .{n}) catch "Custom Layout";
        // Fall back to the static "Custom Layout" (not `candidate`, which
        // points into the stack-local `buf`) on OOM — returning a slice
        // into a buffer about to go out of scope would dangle.
        if (!containsName(existing, candidate)) return allocator.dupe(u8, candidate) catch "Custom Layout";
    }
}

fn containsName(names: [][]const u8, name: []const u8) bool {
    for (names) |n| {
        if (std.mem.eql(u8, n, name)) return true;
    }
    return false;
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

    for (Panels.all()) |p| {
        if (p.closable or l.contains(p.id)) continue;
        try l.insertTabOwned(l.firstLeaf(l.root), 0, p.id);
    }
}

fn ensureParentDir(io: std.Io, path: []const u8) void {
    const dir = std.fs.path.dirname(path) orelse return;
    std.Io.Dir.cwd().createDirPath(io, dir) catch {};
}
