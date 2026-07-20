//! Multi-Document Interface (MDI) — open assets in tabs, layered on top of
//! `EditorState`'s single-scene singleton state without duplicating every
//! panel per tab.
//!
//! Each open asset is a `Document`. `.scene` tabs share the Scene
//! Tree/Viewport/Inspector surface; the *active* one IS `EditorState`'s live
//! scene, parked into a heap snapshot on tab switch and restored on return,
//! so panel state (hierarchy, selection, dirty flag) survives navigation.
//! `.asset` tabs are hosted full-area by their own dedicated editor.
//!
//! Undo history is per-session and resets on tab switch (a single global
//! stack); the dirty indicator persists across switches.
//!
//! This file owns the document model only (open/close/activate/save/dirty) —
//! no drawing. The tab strip UI lives in `DocumentsTabBar.zig` and JSON
//! persistence in `DocumentsPersistence.zig`, both built on this file's
//! `pub` API; `drawTabBar`/`persist`/`restore` below re-export them so
//! callers don't need to know the module is split three ways.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const ProjectOps = @import("../services/ProjectOps.zig");
const EditorCamera = @import("../scene-view/EditorCamera.zig");
const SettingsEditor = @import("../inspector/editor/SettingsEditor.zig");
const ShortcutsEditor = @import("../inspector/editor/ShortcutsEditor.zig");
const DocumentsTabBar = @import("DocumentsTabBar.zig");
const DocumentsPersistence = @import("DocumentsPersistence.zig");

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

    /// The file name (final path component), extension included.
    pub fn name(self: *const Document) []const u8 {
        const p = self.path();
        return if (std.mem.lastIndexOfScalar(u8, p, '/')) |i| p[i + 1 ..] else p;
    }

    /// Tab title: the file name without its extension. The tab already
    /// carries the asset type in its icon, so the extension is redundant and
    /// only eats into the title budget (`TITLE_MAX_KEY`). A leading-dot file
    /// name keeps its whole name.
    pub fn title(self: *const Document) []const u8 {
        const n = self.name();
        const dot = std.mem.lastIndexOfScalar(u8, n, '.') orelse return n;
        return if (dot == 0) n else n[0..dot];
    }
};

var docs: [MAX_DOCS]Document = undefined;
var doc_count: usize = 0;
var active: ?usize = null;

/// Tab whose close is pending a save/discard confirmation (it was dirty).
/// Mutated only here (`requestClose`/`close`); `DocumentsTabBar`'s confirm
/// dialog reads/clears it through `confirmCloseIndex`/`cancelConfirmClose`.
var g_confirm_close: ?usize = null;

// ── Queries ─────────────────────────────────────────────────────────────────

/// Number of open documents.
pub fn count() usize {
    return doc_count;
}

/// Index of the active document, or null if none are open.
pub fn activeIndex() ?usize {
    return active;
}

/// Read-only access to document `i`'s fields/methods, for `DocumentsTabBar`
/// and `DocumentsPersistence` — out-of-range is a programmer error in either
/// of those (both bound their loops by `count()` first).
pub fn docAt(i: usize) *const Document {
    return &docs[i];
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

/// Makes `i` the active document, parking the outgoing scene tab first.
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

/// Closes every document without prompting, discarding unsaved changes.
pub fn closeAll() void {
    for (0..doc_count) |i| {
        if (docs[i].snapshot) |snap| EditorState.gpa.destroy(snap);
    }
    doc_count = 0;
    active = null;
}

/// Move the tab at `from` to position `to`, fixing the active index. Used by
/// `DocumentsTabBar`'s drag-reorder.
pub fn moveTab(from: usize, to: usize) void {
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
    persist();
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

// ── Close-confirmation state ──────────────────────────────────────────────────
// Owned here (mutated by `requestClose`/`close` above); drawn by
// `DocumentsTabBar.drawConfirmClose` through these accessors.

/// Index of the document awaiting a save/discard/cancel decision, if any.
pub fn confirmCloseIndex() ?usize {
    return g_confirm_close;
}

/// Cancels the pending close confirmation without closing the document.
pub fn cancelConfirmClose() void {
    g_confirm_close = null;
}

/// Saves then closes the document pending confirmation.
pub fn confirmCloseAndSave(i: usize) void {
    saveOne(i);
    g_confirm_close = null;
    close(i);
}

/// Closes the document pending confirmation, discarding its changes.
pub fn confirmCloseWithoutSaving(i: usize) void {
    g_confirm_close = null;
    close(i);
}

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

/// Saves document `i` in place (does not close it), activating it first so
/// `ProjectOps`/`SettingsEditor` operate on its data. Most asset kinds have
/// no defined save path yet and are silently skipped. `.studio_settings`
/// saves both editors sharing that tab (`SettingsEditor` and
/// `ShortcutsEditor`), since a rebind alone doesn't touch a settings field.
pub fn saveOne(i: usize) void {
    if (i >= doc_count) return;
    activate(i);
    if (docs[i].kind == .scene) {
        ProjectOps.saveScene(docs[i].path());
        docs[i].dirty = false;
    } else if (docs[i].asset_type == .studio_settings) {
        SettingsEditor.save();
        ShortcutsEditor.save();
    }
}

/// Saves the active document unconditionally, matching the visible Save
/// button rather than gating on the tracked `dirty` flag, which can lag an
/// editor's own state by up to a frame.
pub fn saveActive() void {
    const i = active orelse return;
    saveOne(i);
}

/// Saves every open document with unsaved changes, restoring whichever tab
/// was active beforehand. Ctrl+Shift+S. Unlike `saveActive`, gates on
/// `dirty` — saving every open tab unconditionally would be needless I/O.
pub fn saveAll() void {
    const restore_active = active;
    for (0..doc_count) |i| {
        if (docs[i].dirty) saveOne(i);
    }
    if (restore_active) |a| activate(a);
}

/// Closes the active document (prompting to save first if dirty). Ctrl+W.
pub fn requestCloseActive() void {
    const a = active orelse return;
    requestClose(a);
}

/// Activates the next/previous tab, wrapping around. Ctrl+Tab / Ctrl+Shift+Tab.
pub fn activateAdjacent(forward: bool) void {
    if (doc_count == 0) return;
    const a = active orelse 0;
    const next = if (forward)
        (a + 1) % doc_count
    else
        (a + doc_count - 1) % doc_count;
    activate(next);
}

// ── Re-exports: tab strip UI (`DocumentsTabBar.zig`) ──────────────────────────

pub const drawTabBar = DocumentsTabBar.drawTabBar;

// ── Re-exports: open-document JSON persistence (`DocumentsPersistence.zig`) ──

pub const persist = DocumentsPersistence.persist;
pub const restore = DocumentsPersistence.restore;
