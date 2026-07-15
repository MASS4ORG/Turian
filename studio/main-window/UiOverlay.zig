//! Scene-viewport UI overlay (C3/D9): draws the `.uidoc` documents referenced
//! by the active scene's `ui_document` components, in scene-node order, each
//! as its own independent instance letterboxed into the same viewport —
//! multiple documents never merge. The document currently open in the
//! `.uidoc` editor renders from the editor's live in-memory copy (WYSIWYG
//! while editing); everything else comes from a small mtime-invalidated disk
//! cache. If the edited document isn't referenced by the scene at all it is
//! drawn as well, so the D9 authoring preview works for unreferenced
//! documents too.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const ui_render = @import("ui_render");
const EditorState = @import("../services/EditorState.zig");
const UiDocumentEditor = @import("../inspector/editor/UiDocumentEditor.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const ui = engine.ui;

// ── Disk-document cache (scene-referenced docs not open in the editor) ─────

const MAX_DOCS = 8;

const Entry = struct {
    used: bool = false,
    path_buf: [1024]u8 = undefined,
    path_len: usize = 0,
    mtime: i128 = 0,
    arena: std.heap.ArenaAllocator = .init(std.heap.page_allocator),
    doc: ui.UiDocument = .{},
    events: ui.UiEvents = .init(),
    resolved: []const ?ui.EventId = &.{},

    fn path(self: *const Entry) []const u8 {
        return self.path_buf[0..self.path_len];
    }
};

var entries: [MAX_DOCS]Entry = @splat(.{});

fn fileMtime(path: []const u8) i128 {
    const st = std.Io.Dir.cwd().statFile(gui.io, path, .{}) catch return 0;
    return st.mtime.nanoseconds;
}

/// Load (or fetch cached) `path`, reloading when the file's mtime changes.
/// Returns null while the file is unreadable. Eviction is oldest-slot-wins;
/// MAX_DOCS concurrent scene documents is far beyond the expected 1-2.
fn cachedDoc(path: []const u8) ?*Entry {
    const mtime = fileMtime(path);

    for (&entries) |*e| {
        if (e.used and std.mem.eql(u8, e.path(), path)) {
            if (e.mtime != mtime) loadInto(e, path, mtime);
            return e;
        }
    }

    const slot = blk: {
        for (&entries) |*e| {
            if (!e.used) break :blk e;
        }
        break :blk &entries[0];
    };
    loadInto(slot, path, mtime);
    return slot;
}

fn loadInto(e: *Entry, path: []const u8, mtime: i128) void {
    _ = e.arena.reset(.free_all);
    const a = e.arena.allocator();

    e.used = true;
    const n = @min(path.len, e.path_buf.len);
    @memcpy(e.path_buf[0..n], path[0..n]);
    e.path_len = n;
    e.mtime = mtime;

    e.doc = ui.UiDocument.load(a, gui.io, path) catch .{};
    // Studio preview has no live game registering event types, so intern
    // every named binding first — clicks visibly resolve while authoring
    // (mirrors UiDocumentEditor.load; the M3 runtime stays strict).
    e.events = .init();
    for (e.doc.nodes) |node| {
        for (node.components) |c| {
            if (c != .button) continue;
            switch (c.button.on_click) {
                .named => |name| if (name.len != 0) {
                    _ = e.events.registerName(name);
                },
                // Resolved at dispatch time via GameEventRegistry, not a
                // name intern — nothing to pre-register here.
                .channel => {},
            }
        }
    }
    e.resolved = e.events.resolveDocument(a, &e.doc) catch &.{};
}

// ── Drawing ──────────────────────────────────────────────────────────────────

/// Resolves a texture GUID to file bytes for `ui_render`'s image content,
/// reading into the current dvui frame's arena (freed automatically next
/// frame — matches dvui's own per-frame allocation convention). Shared with
/// `SceneViewport`'s Play-mode UI draw — same asset source, same
/// per-frame-arena lifetime rule.
pub fn resolveTextureBytes(ctx: ?*anyopaque, guid: []const u8) ?[]const u8 {
    _ = ctx;
    const path = EditorState.resolveAssetGuid(guid) orelse return null;
    return std.Io.Dir.cwd().readFileAlloc(gui.io, path, gui.currentWindow().arena(), .unlimited) catch null;
}

/// Authoring feedback: a clicked button has no live game/Frame to visibly
/// react in edit mode, so surface the `on_click` binding it would fire as a
/// toast — independent of whether the name actually resolved.
fn toastButtonClick(doc: *const ui.UiDocument, node_index: usize) void {
    if (node_index >= doc.nodes.len) return;
    const node = doc.nodes[node_index];
    var name: []const u8 = tr("(no on_click binding)");
    for (node.components) |c| {
        if (c != .button) continue;
        switch (c.button.on_click) {
            .named => |n| if (n.len != 0) {
                name = n;
            },
            .channel => |ch| if (ch.slice().len != 0) {
                name = ch.slice();
            },
        }
    }
    const node_name = if (node.name.len != 0) node.name else tr("(node)");
    const msg = StudioLocale.trArgs("UI click: {node} -> {action}", &.{
        .{ .name = "node", .value = .{ .text = node_name } },
        .{ .name = "action", .value = .{ .text = name } },
    });
    gui.toast(@src(), .{ .message = msg });
}

/// Draw one document letterboxed into `target` (viewport-local natural
/// coords). `slot` namespaces dvui ids so several documents drawn from this
/// same call site never collide.
fn drawDocument(
    doc: *const ui.UiDocument,
    resolved: []const ?ui.EventId,
    events: *ui.UiEvents,
    target: gui.Rect,
    slot: usize,
) void {
    var wrap = gui.box(@src(), .{}, .{
        .rect = .{ .x = 0, .y = 0, .w = target.w, .h = target.h },
        .expand = .none,
        .id_extra = slot,
    });
    defer wrap.deinit();

    const lb = ui_render.fit(.{ .w = target.w, .h = target.h }, doc);
    const result = ui_render.drawTree(doc, lb, .{
        .texture_source = resolveTextureBytes,
        .font_source = resolveTextureBytes,
    });
    for (result.clicked()) |node_index| toastButtonClick(doc, node_index);
    // No live game to raise a channel into during this edit-time preview —
    // `toastButtonClick` above already surfaces what a click would fire.
    ui_render.dispatchClicks(doc, result, resolved, events, null);
}

/// The "Show UI overlay" body (C3): scene-referenced documents in scene-node
/// order, plus the currently edited document if the scene doesn't reference
/// it. Call inside the viewport's overlay stack; `target` is the viewport
/// rect in natural coordinates.
pub fn drawSceneOverlay(target: gui.Rect) void {
    const edited_path = UiDocumentEditor.loadedPathPublic();
    var edited_drawn = false;
    var slot: usize = 0;

    for (EditorState.objects[0..EditorState.object_count]) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .ui_document) continue;
            const guid = comp.ui_document.document.slice();
            if (guid.len == 0) continue;
            const path = EditorState.resolveAssetGuid(guid) orelse continue;

            if (edited_path.len != 0 and std.mem.eql(u8, path, edited_path)) {
                // Live in-memory copy: edits show up without saving (D9).
                if (UiDocumentEditor.currentDocument()) |doc| {
                    drawDocument(doc, UiDocumentEditor.currentResolvedIds(), UiDocumentEditor.currentEvents(), target, slot);
                    edited_drawn = true;
                    slot += 1;
                }
            } else if (cachedDoc(path)) |e| {
                drawDocument(&e.doc, e.resolved, &e.events, target, slot);
                slot += 1;
            }
        }
    }

    if (!edited_drawn) {
        if (UiDocumentEditor.currentDocument()) |doc| {
            drawDocument(doc, UiDocumentEditor.currentResolvedIds(), UiDocumentEditor.currentEvents(), target, slot);
        }
    }
}
