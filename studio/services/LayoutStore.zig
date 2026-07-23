//! Persists the dockable panel layout as `layout.json` in the global settings
//! directory. Also owns user-saved layout presets and per-asset-type context
//! layouts (swapped via `setAssetContext`). (De)serializes `DockLayout.Snapshot`
//! with `std.json` and reconciles against the `Panels.all` registry.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const Panels = @import("../main-window/Panels.zig");
const LayoutPresets = @import("LayoutPresets.zig");

pub const DockLayout = gui.DockingWidget.Layout.DockLayout;

const SETTINGS_DIR = ".turian"; // matches editor.Settings' SETTINGS_DIR
const LAYOUT_FILE = "layout.json";
const PRESETS_DIR = "layouts"; // under SETTINGS_DIR

var g_layout: ?DockLayout = null;
var g_path: ?[]u8 = null;
var g_presets_dir: ?[]u8 = null;
var g_global_dir: ?[]u8 = null;
var g_allocator: std.mem.Allocator = undefined;

/// The asset-type layout currently standing in for the main one, if any.
const Context = struct {
    asset_type: editor.AssetType,
    spec: LayoutPresets.AssetLayout,
    layout: DockLayout,
    path: ?[]u8,
};
var g_context: ?Context = null;

/// Loads the saved layout (or builds the default), and remembers `allocator`
/// for `save`/`reset`. Never fails: a missing/corrupt file falls back to the
/// default layout instead of erroring.
pub fn init(allocator: std.mem.Allocator, io: std.Io, global_dir: []const u8) void {
    g_allocator = allocator;
    const sep = std.fs.path.sep_str;
    g_global_dir = allocator.dupe(u8, global_dir) catch null;
    g_path = std.fmt.allocPrint(allocator, "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ LAYOUT_FILE, .{global_dir}) catch null;
    g_presets_dir = std.fmt.allocPrint(allocator, "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ PRESETS_DIR, .{global_dir}) catch null;
    g_layout = loadFromDisk(allocator, io) orelse (LayoutPresets.buildDefault(allocator) catch @panic("OOM building default dock layout"));
}

pub fn deinit(io: std.Io) void {
    save(io);
    clearContext();
    if (g_layout != null) {
        g_layout.?.deinit();
        g_layout = null;
    }
    for ([_]*?[]u8{ &g_path, &g_presets_dir, &g_global_dir }) |slot| {
        if (slot.*) |p| g_allocator.free(p);
        slot.* = null;
    }
}

/// The live layout instance — pass to `dvui.dockspace`. This is the active
/// document's own layout when it has one, else the main layout.
pub fn get() *DockLayout {
    if (g_context) |*c| return &c.layout;
    return &g_layout.?;
}

/// Whether a panel may appear in the layout `get()` currently returns. Always
/// true for the main layout; a context layout admits only the panels its
/// `LayoutPresets.AssetLayout` declares. Handles `"#N"` instance suffixes.
pub fn allows(id: []const u8) bool {
    const c = g_context orelse return true;
    const base = Panels.baseId(id);
    for (c.spec.allowed) |a| {
        if (std.mem.eql(u8, a, base)) return true;
    }
    return false;
}

/// Adds a fresh instance of panel `id` to the current layout's first leaf.
/// No-op if `id` isn't allowed here, or is already open and doesn't permit
/// multiple instances.
pub fn addPanel(id: []const u8, io: std.Io) void {
    if (!allows(id)) return;
    const p = Panels.find(id) orelse return;
    const l = get();
    if (!p.allow_multiple and l.contains(id)) return;
    const instance_id = Panels.newInstanceId(id, l, std.heap.page_allocator) catch return;
    l.insertTabOwned(l.firstLeaf(l.root), 0, instance_id) catch return;
    save(io);
}

/// True while the active document's own layout stands in for the main one.
/// Layout *presets*, built-in and user-saved alike, are arrangements of the
/// scene panels — so the View ▸ Layout menu hides itself in this state rather
/// than offer to apply one whose panels the live layout wouldn't admit.
pub fn hasAssetContext() bool {
    return g_context != null;
}

/// Switches the live layout to `asset_type`'s own arrangement, or back to the
/// main one when `asset_type` is null or has no arrangement of its own. Cheap
/// and idempotent — safe to call every frame from the frame loop, which is how
/// the layout follows the active document tab.
pub fn setAssetContext(asset_type: ?editor.AssetType, io: std.Io) void {
    const spec: ?LayoutPresets.AssetLayout = if (asset_type) |t| LayoutPresets.forAssetType(t) else null;

    if (spec == null) {
        if (g_context == null) return;
        save(io);
        clearContext();
        return;
    }
    if (g_context) |c| {
        if (c.asset_type == asset_type.?) return;
        save(io);
        clearContext();
    }

    const t = asset_type.?;
    const path = contextPath(t);
    const saved = if (path) |p| loadFromPath(g_allocator, io, p, spec.?.allowed) else null;
    const layout = saved orelse spec.?.build(g_allocator) catch {
        if (path) |p| g_allocator.free(p);
        return;
    };
    g_context = .{ .asset_type = t, .spec = spec.?, .layout = layout, .path = path };
}

fn clearContext() void {
    if (g_context) |*c| {
        c.layout.deinit();
        if (c.path) |p| g_allocator.free(p);
    }
    g_context = null;
}

fn contextPath(asset_type: editor.AssetType) ?[]u8 {
    const dir = g_global_dir orelse return null;
    const sep = std.fs.path.sep_str;
    return std.fmt.allocPrint(
        g_allocator,
        "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ "layout-{t}.json",
        .{ dir, asset_type },
    ) catch null;
}

/// Discards the current layout and replaces it with `new_layout` (already
/// built/loaded by the caller — a preset, typically), saving it in its place.
/// Takes ownership of `new_layout`.
pub fn replace(new_layout: DockLayout, io: std.Io) void {
    if (g_context) |*c| {
        c.layout.deinit();
        c.layout = new_layout;
    } else {
        if (g_layout) |*l| l.deinit();
        g_layout = new_layout;
    }
    save(io);
}

/// Serializes `l` to JSON text (owned by `g_allocator`; caller frees), or null
/// on failure.
fn serialize(l: *const DockLayout) ?[]u8 {
    const snap = l.snapshot(g_allocator) catch |err| {
        std.log.warn("LayoutStore: failed to snapshot layout: {t}", .{err});
        return null;
    };
    defer snap.deinit(g_allocator);
    return std.json.Stringify.valueAlloc(g_allocator, snap, .{ .whitespace = .indent_2 }) catch |err| {
        std.log.warn("LayoutStore: failed to serialize layout: {t}", .{err});
        return null;
    };
}

/// Serializes and writes the live layout — the active document's own, if it
/// has one, else the main one — to its own file. Logs and gives up on failure
/// (I/O errors here shouldn't take down the editor).
pub fn save(io: std.Io) void {
    if (g_context) |*c| {
        writeLayout(io, c.path orelse return, &c.layout);
        return;
    }
    writeLayout(io, g_path orelse return, if (g_layout) |*l| l else return);
}

fn writeLayout(io: std.Io, path: []const u8, l: *const DockLayout) void {
    const text = serialize(l) orelse return;
    defer g_allocator.free(text);

    ensureParentDir(io, path);
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = text }) catch |err| {
        std.log.warn("LayoutStore: failed to write {s}: {t}", .{ path, err });
    };
}

/// Discards the live layout and rebuilds + saves its default arrangement:
/// the active document's own default when one is in effect, else the main
/// `LayoutPresets.buildDefault`.
pub fn reset(io: std.Io) void {
    if (g_context) |*c| {
        const rebuilt = c.spec.build(g_allocator) catch return;
        c.layout.deinit();
        c.layout = rebuilt;
    } else {
        if (g_layout) |*l| l.deinit();
        g_layout = LayoutPresets.buildDefault(g_allocator) catch @panic("OOM building default dock layout");
    }
    save(io);
}

fn loadFromDisk(allocator: std.mem.Allocator, io: std.Io) ?DockLayout {
    const path = g_path orelse return null;
    return loadFromPath(allocator, io, path, null);
}

/// Reads a layout file. `allowed` (null for the main layout) restricts which
/// panels may survive — see `reconcile`.
fn loadFromPath(allocator: std.mem.Allocator, io: std.Io, path: []const u8, allowed: ?[]const []const u8) ?DockLayout {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return null;
    defer allocator.free(content);

    const parsed = std.json.parseFromSlice(DockLayout.Snapshot, allocator, content, .{}) catch |err| {
        std.log.warn("LayoutStore: failed to parse {s} ({t})", .{ path, err });
        return null;
    };
    defer parsed.deinit();

    var l = DockLayout.fromSnapshot(allocator, parsed.value) catch |err| {
        std.log.warn("LayoutStore: failed to rebuild {s} ({t})", .{ path, err });
        return null;
    };
    reconcile(&l, allocator, allowed) catch {
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

    const text = serialize(l) orelse return;
    defer g_allocator.free(text);

    std.Io.Dir.cwd().createDirPath(io, dir_path) catch {};
    const path = std.fmt.allocPrint(g_allocator, "{s}" ++ std.fs.path.sep_str ++ "{s}.json", .{ dir_path, name }) catch return;
    defer g_allocator.free(path);
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = text }) catch |err| {
        std.log.warn("LayoutStore: failed to write preset {s}: {t}", .{ path, err });
    };
}

/// Loads a previously-saved preset by name, reconciled against the current
/// panel registry like the main `layout.json`. Null if missing/corrupt.
pub fn loadPreset(name: []const u8, allocator: std.mem.Allocator, io: std.Io) ?DockLayout {
    const dir_path = g_presets_dir orelse return null;
    const path = std.fmt.allocPrint(allocator, "{s}" ++ std.fs.path.sep_str ++ "{s}.json", .{ dir_path, name }) catch return null;
    defer allocator.free(path);
    return loadFromPath(allocator, io, path, null);
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

/// Drops tabs whose slug isn't a known panel (e.g. from an older/newer Studio
/// version) or isn't `allowed` here, then restores whatever the layout is
/// required to contain: the non-closable panels for the main layout (`allowed`
/// null), or the anchor panel — `allowed[0]` — for a context layout, so
/// neither can come back empty.
fn reconcile(l: *DockLayout, allocator: std.mem.Allocator, allowed: ?[]const []const u8) !void {
    var to_drop: std.ArrayList([]const u8) = .empty;
    defer to_drop.deinit(allocator);
    for (l.nodes.items) |n| {
        switch (n) {
            .leaf => |leaf| for (leaf.tabs.items) |slug| {
                if (Panels.find(slug) == null or !slugAllowed(slug, allowed)) try to_drop.append(allocator, slug);
            },
            .split, .free => {},
        }
    }
    for (to_drop.items) |slug| l.removePanel(slug);

    if (allowed) |a| {
        if (a.len > 0 and !l.contains(a[0])) try l.insertTabOwned(l.firstLeaf(l.root), 0, a[0]);
        return;
    }
    for (Panels.all()) |p| {
        if (p.closable or l.contains(p.id)) continue;
        try l.insertTabOwned(l.firstLeaf(l.root), 0, p.id);
    }
}

fn slugAllowed(slug: []const u8, allowed: ?[]const []const u8) bool {
    const a = allowed orelse return true;
    const base = Panels.baseId(slug);
    for (a) |id| {
        if (std.mem.eql(u8, id, base)) return true;
    }
    return false;
}

fn ensureParentDir(io: std.Io, path: []const u8) void {
    const dir = std.fs.path.dirname(path) orelse return;
    std.Io.Dir.cwd().createDirPath(io, dir) catch {};
}
