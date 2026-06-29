//! Multi-Document Interface (MDI) — open assets in tabs.
//!
//! The editor historically edited a single scene at a time, backed by the
//! singleton state in `EditorState` (`objects`, `selected_object`, …). This
//! module layers a tabbed document interface on top of that singleton without
//! duplicating every panel:
//!
//!   * Each open asset is a `Document` (a tab). Two kinds exist:
//!       - `.scene`  — a scene/prefab edited in the Scene Tree + Viewport +
//!                     object Inspector (the existing 3-pane surface).
//!       - `.asset`  — a material / input-actions / project-settings / data
//!                     asset edited by its dedicated editor, hosted full-area.
//!   * The *active* scene tab IS `EditorState`'s live scene. Switching tabs
//!     parks the outgoing scene into a heap snapshot and restores the incoming
//!     one, so panel state (hierarchy, selection, dirty flag) survives tab
//!     navigation. This is the "reuse the panels, preserve their state"
//!     alternative called out in the issue — chosen over duplicating every
//!     panel per tab, which would multiply the dvui widget tree and the
//!     renderer/gizmo/play wiring for little gain.
//!
//! Undo history is intentionally per-session and reset on tab switch (the undo
//! stack is a single global); the dirty indicator is preserved across switches.
//! Drag-and-drop *between* separate tab areas (docking / split views) is left as
//! future work — reordering within the one tab strip is supported.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const ProjectOps = @import("ProjectOps.zig");
const EditorCamera = @import("EditorCamera.zig");

pub const MAX_DOCS = 32;

pub const Kind = enum { scene, asset };

/// Heap snapshot of the singleton scene-editing state for an *inactive* scene
/// tab. Allocated lazily the first time a scene tab is switched away from.
const SceneSnapshot = struct {
    objects: [EditorState.MAX_OBJECTS]EditorState.SceneNode = undefined,
    object_count: usize = 0,
    selected_object: ?usize = null,
    selected_set: [EditorState.MAX_OBJECTS]bool = .{false} ** EditorState.MAX_OBJECTS,
    last_select_idx: ?usize = null,
    dirty: bool = false,
    /// Free-look viewport pose, so each scene tab keeps its own camera.
    cam: EditorCamera.State = .{},
};

pub const Document = struct {
    kind: Kind,
    asset_type: editor.AssetType,
    path_buf: [1024]u8 = undefined,
    path_len: usize = 0,
    /// Owned scene snapshot. Non-null only for inactive scene tabs.
    snapshot: ?*SceneSnapshot = null,
    /// Unsaved-changes flag. For the active scene tab this is refreshed from
    /// `EditorState.scene_dirty` each frame (see `syncActiveDirty`).
    dirty: bool = false,

    pub fn path(self: *const Document) []const u8 {
        return self.path_buf[0..self.path_len];
    }

    /// Tab title: the file name (final path component).
    pub fn name(self: *const Document) []const u8 {
        const p = self.path();
        return if (std.mem.lastIndexOfScalar(u8, p, '/')) |i| p[i + 1 ..] else p;
    }
};

var docs: [MAX_DOCS]Document = undefined;
var doc_count: usize = 0;
var active: ?usize = null;

/// Index of the tab currently being dragged for reorder, if any.
var g_drag_tab: ?usize = null;
/// Per-frame cache of each tab's physical rect, used for reorder hit-testing.
var tab_rects: [MAX_DOCS]gui.Rect.Physical = undefined;

/// Index of the leftmost tab currently shown; the strip pages by this when more
/// tabs are open than fit the window (the ‹ › nav buttons adjust it).
var first_tab: usize = 0;
/// Per-tab physical width measured last frame, used to decide how many tabs fit.
var tab_w: [MAX_DOCS]f32 = .{0} ** MAX_DOCS;
/// Tab whose close is pending a save/discard confirmation (it was dirty).
var g_confirm_close: ?usize = null;

/// Assumed physical width for a tab whose real width isn't known yet (never
/// drawn). Conservative so paging never overpacks the strip on the first frame.
const DEFAULT_TAB_W: f32 = 140;

/// Fixed height of the tab strip — reserved even when no tabs are open so the
/// editor layout below it never jumps as the last tab closes.
const TAB_STRIP_H: f32 = 30;
/// Size of the per-tab close button (always laid out — only its paint toggles —
/// so the tab width doesn't change on hover).
const CLOSE_SLOT: f32 = 18;

/// Settings key + bounds for the max displayed tab-title length.
const TITLE_MAX_KEY = "editor.tab_title_max";
const TITLE_MAX_DEFAULT: i64 = 18;
const TITLE_MIN: i64 = 6;

// ── Queries ─────────────────────────────────────────────────────────────────

pub fn count() usize {
    return doc_count;
}

pub fn activeIndex() ?usize {
    return active;
}

/// True when the active document is hosted by a dedicated asset editor (so the
/// caller draws that editor full-area instead of the scene 3-pane surface).
pub fn activeIsAsset() bool {
    const a = active orelse return false;
    return docs[a].kind == .asset;
}

/// Path + type of the active asset document (only valid when `activeIsAsset`).
pub fn activePath() []const u8 {
    const a = active orelse return "";
    return docs[a].path();
}

pub fn activeAssetType() editor.AssetType {
    const a = active orelse return .unknown;
    return docs[a].asset_type;
}

fn find(full_path: []const u8) ?usize {
    for (0..doc_count) |i| {
        if (std.mem.eql(u8, docs[i].path(), full_path)) return i;
    }
    return null;
}

fn setPath(d: *Document, full_path: []const u8) void {
    const len = @min(full_path.len, d.path_buf.len);
    @memcpy(d.path_buf[0..len], full_path[0..len]);
    d.path_len = len;
}

// ── Open ──────────────────────────────────────────────────────────────────────

/// Open (or focus) a scene/prefab asset in a tab and load it into the editor.
pub fn openScene(full_path: []const u8) void {
    if (find(full_path)) |i| {
        activate(i);
        return;
    }
    if (doc_count >= MAX_DOCS) return;

    parkActiveScene();

    const idx = doc_count;
    docs[idx] = .{ .kind = .scene, .asset_type = .scene };
    setPath(&docs[idx], full_path);
    doc_count += 1;
    active = idx;

    // Loads into EditorState, sets current_scene_path, clears undo, marks saved.
    _ = ProjectOps.loadScene(full_path);
    docs[idx].dirty = EditorState.scene_dirty;
    docs[idx].snapshot = null; // live in EditorState while active
    // Re-seed the viewport camera from this scene rather than inheriting the
    // previous tab's pose.
    EditorCamera.reset();

    persist();
}

/// Open (or focus) a non-scene asset in a tab, hosted by its dedicated editor.
pub fn openAsset(full_path: []const u8, asset_type: editor.AssetType) void {
    if (find(full_path)) |i| {
        activate(i);
        return;
    }
    if (doc_count >= MAX_DOCS) return;

    parkActiveScene();

    const idx = doc_count;
    docs[idx] = .{ .kind = .asset, .asset_type = asset_type };
    setPath(&docs[idx], full_path);
    doc_count += 1;
    active = idx;

    EditorState.selectAsset(full_path);
    persist();
}

// ── Activate / close / reorder ────────────────────────────────────────────────

pub fn activate(i: usize) void {
    if (i >= doc_count) return;
    if (active) |a| {
        if (a == i) return;
        parkActiveScene();
    }
    loadDoc(i);
    persist();
}

/// Capture the live scene into the active scene tab's snapshot before its state
/// is clobbered by switching to another document.
fn parkActiveScene() void {
    const a = active orelse return;
    if (docs[a].kind != .scene) return;
    const snap = ensureSnapshot(a);
    snap.objects = EditorState.objects;
    snap.object_count = EditorState.object_count;
    snap.selected_object = EditorState.selected_object;
    snap.selected_set = EditorState.selected_set;
    snap.last_select_idx = EditorState.last_select_idx;
    snap.dirty = EditorState.scene_dirty;
    snap.cam = EditorCamera.getState();
    docs[a].dirty = EditorState.scene_dirty;
}

/// Make `i` the active document, restoring its scene state (for scene tabs) or
/// selecting its asset (for asset tabs).
fn loadDoc(i: usize) void {
    active = i;
    const d = &docs[i];
    if (d.kind == .scene) {
        if (d.snapshot) |snap| restoreScene(snap, d.path());
    } else {
        EditorState.selectAsset(d.path());
    }
}

fn restoreScene(snap: *const SceneSnapshot, scene_path: []const u8) void {
    EditorState.objects = snap.objects;
    EditorState.object_count = snap.object_count;
    EditorState.selected_object = snap.selected_object;
    EditorState.selected_set = snap.selected_set;
    EditorState.last_select_idx = snap.last_select_idx;
    EditorState.clearUndoStack();
    EditorState.scene_dirty = snap.dirty;
    // No undo history after a tab switch; keep the dirty flag honest.
    EditorState.saved_undo_depth = if (snap.dirty) null else 0;
    EditorState.setCurrentScenePath(scene_path);
    EditorState.clearSelectedAsset();
    EditorCamera.setState(snap.cam);
    // Rebind component definitions in case the registry hot-reloaded meanwhile.
    EditorState.syncSceneWithDefinitions();
}

/// User-initiated close. Dirty documents first prompt a save/discard/cancel
/// confirmation; clean ones close immediately.
pub fn requestClose(i: usize) void {
    if (i >= doc_count) return;
    if (docs[i].dirty) {
        g_confirm_close = i;
        return;
    }
    close(i);
}

pub fn close(i: usize) void {
    if (i >= doc_count) return;

    // Fully discard the document: free its snapshot and drop it from the array.
    if (docs[i].snapshot) |snap| EditorState.gpa.destroy(snap);

    const was_active = (active != null and active.? == i);

    for (i..doc_count - 1) |k| docs[k] = docs[k + 1];
    doc_count -= 1;

    if (doc_count == 0) {
        active = null;
        // Nothing is open: wipe the editing surface so the hierarchy, viewport
        // and inspector don't keep showing the closed document's contents.
        EditorState.clearSelectedAsset();
        EditorState.clearScene();
        EditorCamera.reset();
    } else if (was_active) {
        active = null; // discard the closed doc's live state, then load neighbor
        const ni = if (i < doc_count) i else doc_count - 1;
        loadDoc(ni);
    } else if (active) |a| {
        if (a > i) active = a - 1;
    }
    if (g_confirm_close) |ci| {
        // Keep the pending-confirmation index valid after the array shifts.
        if (ci == i) g_confirm_close = null else if (ci > i) g_confirm_close = ci - 1;
    }
    persist();
}

pub fn closeAll() void {
    for (0..doc_count) |i| {
        if (docs[i].snapshot) |snap| EditorState.gpa.destroy(snap);
    }
    doc_count = 0;
    active = null;
    g_drag_tab = null;
}

/// Move the tab at `from` to position `to`, fixing the active index.
fn move(from: usize, to: usize) void {
    if (from == to or from >= doc_count or to >= doc_count) return;
    const tmp = docs[from];
    if (from < to) {
        for (from..to) |k| docs[k] = docs[k + 1];
    } else {
        var k = from;
        while (k > to) : (k -= 1) docs[k] = docs[k - 1];
    }
    docs[to] = tmp;

    if (active) |a| {
        if (a == from) {
            active = to;
        } else if (from < a and a <= to) {
            active = a - 1;
        } else if (to <= a and a < from) {
            active = a + 1;
        }
    }
    g_drag_tab = to;
    persist();
}

/// Last-measured physical width of tab `k`, or a conservative default if it
/// hasn't been drawn yet.
fn widthOf(k: usize) f32 {
    return if (tab_w[k] > 0) tab_w[k] else DEFAULT_TAB_W;
}

/// First tab index (exclusive) past those that fit in `avail` physical pixels
/// starting at `start`. Always shows at least the `start` tab.
fn fitEnd(start: usize, avail: f32) usize {
    var used: f32 = 0;
    var k = start;
    while (k < doc_count) : (k += 1) {
        used += widthOf(k);
        if (used > avail and k > start) return k;
    }
    return doc_count;
}

fn ensureSnapshot(i: usize) *SceneSnapshot {
    if (docs[i].snapshot) |s| return s;
    const s = EditorState.gpa.create(SceneSnapshot) catch {
        // Allocation failed — fall back to a static scratch snapshot. Switching
        // away will at worst lose this tab's parked edits, never crash.
        return &fallback_snapshot;
    };
    s.* = .{};
    docs[i].snapshot = s;
    return s;
}
var fallback_snapshot: SceneSnapshot = .{};

// ── Dirty tracking ────────────────────────────────────────────────────────────

/// Refresh the active scene tab's dirty flag from the live editor state. Call
/// once per frame before drawing the tab bar.
pub fn syncActiveDirty() void {
    const a = active orelse return;
    if (docs[a].kind == .scene) docs[a].dirty = EditorState.scene_dirty;
}

/// Mark the active document dirty/clean (used by asset editors that track edits).
pub fn setActiveDirty(dirty: bool) void {
    const a = active orelse return;
    docs[a].dirty = dirty;
}

fn isDocDirty(i: usize) bool {
    return docs[i].dirty;
}

// ── Tab bar UI ────────────────────────────────────────────────────────────────

/// Draw the document tab strip. `mouse_held` is whether the left mouse button is
/// currently down (used to drive drag-reordering). The strip keeps a fixed
/// height even with no tabs open, so the editor below it never shifts.
pub fn drawTabBar(mouse_held: bool) void {
    syncActiveDirty();

    // When empty the bar is an invisible placeholder: it still reserves
    // TAB_STRIP_H so the editor below never shifts as the last tab closes.
    const has_docs = doc_count > 0;
    var bar = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .background = has_docs,
        .style = .window,
        .min_size_content = .{ .h = TAB_STRIP_H },
        .padding = .{ .x = 4, .y = 2, .w = 4, .h = 0 },
    });
    defer bar.deinit();

    if (!has_docs) return;

    const cfg_max = if (EditorState.settingsReady())
        EditorState.settings.getInt(TITLE_MAX_KEY, TITLE_MAX_DEFAULT)
    else
        TITLE_MAX_DEFAULT;
    title_max_cache = @intCast(@max(TITLE_MIN, cfg_max));

    var to_close: ?usize = null;
    var to_activate: ?usize = null;

    // ── Paging: decide which tabs are visible ─────────────────────────────────
    // Work in physical pixels (tab widths are measured from physical rects).
    const scale = gui.windowNaturalScale();
    const content_w = bar.data().contentRectScale().r.w;

    var total_w: f32 = 0;
    for (0..doc_count) |k| total_w += widthOf(k);

    // Overflow only matters once we know the bar's width (content_w > 0). Until
    // then (first frame) treat space as unlimited so every tab is drawn.
    const known_w = content_w > 1;
    const needs_nav = known_w and total_w > content_w + 1;
    const nav_reserve: f32 = if (needs_nav) 64 * scale else 0;
    const avail_tabs: f32 = if (known_w) @max(0, content_w - nav_reserve) else 1e9;

    if (first_tab >= doc_count) first_tab = if (doc_count > 0) doc_count - 1 else 0;
    // Keep the active tab on screen.
    if (active) |a| {
        if (a < first_tab) first_tab = a;
        while (first_tab < a and fitEnd(first_tab, avail_tabs) <= a) first_tab += 1;
    }

    const end = fitEnd(first_tab, avail_tabs);
    const show_left = first_tab > 0;
    const show_right = end < doc_count;

    if (show_left) {
        if (gui.buttonIcon(@src(), "tabs_left", gui.entypo.chevron_left, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 14, .h = 14 },
            .padding = .all(2),
        })) {
            if (first_tab > 0) first_tab -= 1;
        }
    }

    {
        var strip = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_y = 1.0, .expand = .horizontal });
        defer strip.deinit();

        var i: usize = first_tab;
        while (i < end) : (i += 1) {
            drawTab(i, mouse_held, &to_activate, &to_close);
        }
    }

    if (show_right) {
        if (gui.buttonIcon(@src(), "tabs_right", gui.entypo.chevron_right, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 14, .h = 14 },
            .padding = .all(2),
        })) {
            if (first_tab + 1 < doc_count) first_tab += 1;
        }
    }

    // Drag-reorder: while a tab is held, step it one slot toward the cursor when
    // the cursor crosses the neighbour's midpoint. Stepping (rather than jumping
    // to the cursor's tab) avoids oscillation when tab widths differ. Only swap
    // between currently-visible tabs.
    if (!mouse_held) {
        g_drag_tab = null;
    } else if (g_drag_tab) |di| if (di >= first_tab and di < end) {
        const mx = gui.currentWindow().mouse_pt.x;
        if (di + 1 < end and mx > tab_rects[di + 1].x + tab_rects[di + 1].w / 2) {
            move(di, di + 1);
        } else if (di > first_tab and mx < tab_rects[di - 1].x + tab_rects[di - 1].w / 2) {
            move(di, di - 1);
        }
    };

    // Apply activation only if it wasn't actually a close-button click.
    if (to_activate) |ci| {
        if (to_close == null) activate(ci);
    }
    if (to_close) |ci| requestClose(ci);

    // Floating ghost + the save/discard/cancel modal are drawn last so they
    // layer above the strip.
    drawDragGhost(mouse_held);
    drawConfirmClose();
}

/// Draw a single tab. Sets `*to_activate` / `*to_close` for the caller to apply
/// after the strip is laid out.
fn drawTab(i: usize, mouse_held: bool, to_activate: *?usize, to_close: *?usize) void {
    const is_active = (active != null and active.? == i);
    const is_dragged = (g_drag_tab != null and g_drag_tab.? == i and mouse_held);

    var tab = gui.box(@src(), .{ .dir = .horizontal }, .{
        .id_extra = i,
        .background = true,
        // A dragged tab lifts to the highlight style as a drag affordance.
        .style = if (is_active or is_dragged) .highlight else .window,
        .border = .all(1),
        .corner_radius = .{ .x = 4, .y = 4, .w = 0, .h = 0 },
        .padding = .{ .x = 8, .y = 4, .w = 4, .h = 4 },
        .margin = .{ .w = 2 },
        .gravity_y = 1.0,
        .min_size_content = .{ .w = 60 },
    });
    defer tab.deinit();

    const tr = tab.data().rectScale().r;
    tab_rects[i] = tr;
    // Record width (incl. the inter-tab margin) for next frame's paging maths.
    tab_w[i] = tr.w + 4 * gui.windowNaturalScale();
    const hovered = tr.contains(gui.currentWindow().mouse_pt);

    // Left-press activates + starts a reorder drag; middle-press closes the tab.
    // Neither is `e.handle`d so the close button can still receive its clicks.
    for (gui.events()) |*e| {
        if (!gui.eventMatchSimple(e, tab.data())) continue;
        switch (e.evt) {
            .mouse => |me| {
                if (me.action == .press and me.button == .left) {
                    to_activate.* = i;
                    g_drag_tab = i;
                } else if (me.action == .press and me.button == .middle) {
                    to_close.* = i;
                }
            },
            else => {},
        }
    }

    // Unsaved-changes indicator.
    if (isDocDirty(i)) {
        gui.label(@src(), "*", .{}, .{
            .id_extra = i,
            .gravity_y = 0.5,
            .padding = .{ .w = 4 },
            .font = .theme(.body),
        });
    }

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "{s}", .{trimTitle(docs[i].name(), title_max_cache, &name_buf)}, .{
        .id_extra = i,
        .gravity_y = 0.5,
    });

    // The close button is *always* laid out (so the tab width never changes),
    // but is made invisible unless the tab is hovered or active. It stays
    // clickable — if you're clicking it you're hovering, so it's visible.
    const show_close = hovered or is_active;
    if (gui.buttonIcon(@src(), "close", gui.entypo.cross, .{}, .{}, .{
        .id_extra = i,
        .gravity_y = 0.5,
        .min_size_content = .{ .w = CLOSE_SLOT, .h = CLOSE_SLOT },
        .margin = .{ .x = 4 },
        .padding = .all(2),
        .background = show_close,
        .color_text = if (show_close) null else gui.Color.transparent,
    })) {
        to_close.* = i;
    }
}

/// `title_max` resolved once per frame in drawTabBar and read by `drawTab`.
var title_max_cache: usize = @intCast(TITLE_MAX_DEFAULT);

/// Trim `name` to at most `max` characters, appending an ellipsis when cut.
fn trimTitle(name: []const u8, max: usize, buf: []u8) []const u8 {
    if (name.len <= max or max < 2) return name;
    const keep = max - 1;
    const n = @min(keep, buf.len - 3);
    @memcpy(buf[0..n], name[0..n]);
    // U+2026 HORIZONTAL ELLIPSIS
    buf[n] = 0xE2;
    buf[n + 1] = 0x80;
    buf[n + 2] = 0xA6;
    return buf[0 .. n + 3];
}

/// Small label that follows the cursor while a tab is being dragged, so the user
/// can see the drag is active.
fn drawDragGhost(mouse_held: bool) void {
    if (!mouse_held) return;
    const di = g_drag_tab orelse return;
    if (di >= doc_count) return;

    gui.cursorSet(.arrow_all);

    const mp = gui.currentWindow().mouse_pt;
    const scale = gui.windowNaturalScale();
    g_ghost_rect.x = mp.x / scale + 12;
    g_ghost_rect.y = mp.y / scale + 12;

    var fw = gui.floatingWindow(@src(), .{
        .rect = &g_ghost_rect,
        .resize = .none,
        .stay_above_parent_window = true,
        .window_avoid = .none,
    }, .{
        .background = true,
        .style = .highlight,
        .border = .all(1),
        .corner_radius = .all(4),
        .padding = .all(4),
    });
    defer fw.deinit();

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "{s}", .{trimTitle(docs[di].name(), title_max_cache, &name_buf)}, .{ .gravity_y = 0.5 });
}
var g_ghost_rect: gui.Rect = .{ .x = 0, .y = 0, .w = 0, .h = 0 };

/// Modal save/discard/cancel prompt shown when closing a document with unsaved
/// changes.
fn drawConfirmClose() void {
    const i = g_confirm_close orelse return;
    if (i >= doc_count) {
        g_confirm_close = null;
        return;
    }

    var win = gui.floatingWindow(@src(), .{
        .modal = true,
        .center_on = gui.currentWindow().subwindows.current_rect,
        .window_avoid = .nudge,
    }, .{ .role = .dialog, .min_size_content = .{ .w = 320 } });
    defer win.deinit();

    var open_flag = true;
    win.dragAreaSet(gui.windowHeader("Unsaved Changes", "", &open_flag));
    if (!open_flag) {
        g_confirm_close = null;
        return;
    }

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "Save changes to \"{s}\" before closing?", .{
        trimTitle(docs[i].name(), 48, &name_buf),
    }, .{ .padding = .all(8) });

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_x = 1.0, .padding = .all(4) });
    defer row.deinit();

    if (gui.button(@src(), "Save", .{}, .{})) {
        // Load the doc so EditorState holds its scene, then save + close.
        activate(i);
        if (docs[i].kind == .scene) ProjectOps.saveScene(docs[i].path());
        g_confirm_close = null;
        close(i);
    }
    if (gui.button(@src(), "Don't Save", .{}, .{ .id_extra = 1 })) {
        const idx = i;
        g_confirm_close = null;
        close(idx);
    }
    if (gui.button(@src(), "Cancel", .{}, .{ .id_extra = 2 })) {
        g_confirm_close = null;
    }
}

// ── Persistence ──────────────────────

const OPEN_KEY = "editor.open_documents";

/// Persist the open-document list (project-relative paths) + active index into
/// settings, scoped to the current project. Saved to disk on editor shutdown.
/// True while `restore` is replaying tabs, so the per-open `persist` calls don't
/// clobber the settings value we're still reading from.
var restoring: bool = false;

pub fn persist() void {
    if (restoring) return;
    if (!EditorState.settingsReady()) return;
    const proj = EditorState.project_path orelse return;

    const a = EditorState.gpa;
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(a);

    out.appendSlice(a, "{\"project\":") catch return;
    writeJsonString(a, &out, proj) catch return;
    var num_buf: [32]u8 = undefined;
    const num = std.fmt.bufPrint(&num_buf, ",\"active\":{d},\"docs\":[", .{active orelse 0}) catch return;
    out.appendSlice(a, num) catch return;
    for (0..doc_count) |i| {
        if (i > 0) out.appendSlice(a, ",") catch return;
        writeJsonString(a, &out, relativeTo(proj, docs[i].path())) catch return;
    }
    out.appendSlice(a, "]}") catch return;

    EditorState.settings.set(OPEN_KEY, out.items) catch {};
}

/// Restore the previously-open tabs for the just-opened project. Called from
/// `ProjectOps.openProject` after the project's component registry is ready.
pub fn restore() void {
    closeAll();
    if (!EditorState.settingsReady()) return;
    const proj = EditorState.project_path orelse return;

    const raw_ref = EditorState.settings.get(OPEN_KEY) orelse return;
    const arena = gui.currentWindow().arena();
    // Copy out of settings memory: opening tabs below triggers settings writes
    // that can invalidate `raw_ref` (and JSON strings reference their source).
    const raw = arena.dupe(u8, raw_ref) catch return;
    const parsed = std.json.parseFromSlice(std.json.Value, arena, raw, .{}) catch return;

    restoring = true;
    defer {
        restoring = false;
        persist();
    }
    if (parsed.value != .object) return;
    const obj = parsed.value.object;

    // Only restore tabs that belong to the project being opened.
    const saved_proj = obj.get("project") orelse return;
    if (saved_proj != .string or !std.mem.eql(u8, saved_proj.string, proj)) return;

    const docs_val = obj.get("docs") orelse return;
    if (docs_val != .array) return;

    for (docs_val.array.items) |item| {
        if (item != .string) continue;
        var path_buf: [1024]u8 = undefined;
        const full = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ proj, item.string }) catch continue;
        if (!fileExists(full)) continue;
        const at = editor.asset_registry.lookupByFilename(full);
        if (at == .scene) openScene(full) else openAsset(full, at);
    }

    if (obj.get("active")) |av| {
        if (av == .integer) {
            const ai: usize = @intCast(@max(0, av.integer));
            if (ai < doc_count) activate(ai);
        }
    }
}

fn fileExists(full: []const u8) bool {
    var f = std.Io.Dir.cwd().openFile(gui.io, full, .{}) catch return false;
    f.close(gui.io);
    return true;
}

/// Strip the `<proj>/` prefix from `full`, yielding a project-relative path.
fn relativeTo(proj: []const u8, full: []const u8) []const u8 {
    if (full.len > proj.len + 1 and std.mem.startsWith(u8, full, proj) and full[proj.len] == '/') {
        return full[proj.len + 1 ..];
    }
    return full;
}

fn writeJsonString(a: std.mem.Allocator, out: *std.ArrayList(u8), s: []const u8) !void {
    try out.append(a, '"');
    for (s) |c| switch (c) {
        '"' => try out.appendSlice(a, "\\\""),
        '\\' => try out.appendSlice(a, "\\\\"),
        '\n' => try out.appendSlice(a, "\\n"),
        '\r' => try out.appendSlice(a, "\\r"),
        else => try out.append(a, c),
    };
    try out.append(a, '"');
}
