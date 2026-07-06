//! `.uidoc` document editing state + the three panels that present it,
//! Unity/Godot-style: opening a `.uidoc` tab swaps the scene's Hierarchy and
//! Viewport panels for `drawHierarchyPanel`/`drawViewPanel` (wired in
//! `Window.zig`), and the Inspector shows the selected node's properties via
//! `drawInspector` (wired in `Inspector.zig`). Merely *selecting* a `.uidoc`
//! in the Asset Browser (not opening a tab) shows only `drawGlobalSettings`
//! through the ordinary `EditorRegistry` per-asset-type path — same as any
//! other asset type gets when clicked without being opened.
//!
//! Editing mutates an in-memory copy of the document, arena-owned and
//! reloaded when the active asset path changes (mirrors `MaterialEditor`'s
//! "loaded lazily when selection path changes"). Structural edits (add/
//! remove node or component) are recorded as `cmd_*` values and applied
//! once per frame, after `drawHierarchyPanel`'s tree (mirrors
//! `InputActionsEditor`'s "mutations recorded as cmd_* vars + applied AFTER
//! draw loop — never mutate while iterating").
//! Every component body goes through `PropDraw.drawComponentAlloc`'s generic
//! reflection dispatch (C2) — strings via `drawStringEdit`, texture refs via
//! the `TypedAssetRef` drag-drop/picker drawer, event bindings via the
//! `EventBinding` drawer. Adding a field to a `UiComponent` variant shows up
//! here with zero `studio/` changes.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const ui_render = @import("ui_render");
const EditorState = @import("EditorState.zig");
const PropDraw = @import("PropDraw.zig");
const tree_view = @import("TreeView.zig");

const ui = engine.ui;

// ── Loaded-document state (persists across frames) ──────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;
var arena: std.heap.ArenaAllocator = .init(std.heap.page_allocator);
var doc: ui.UiDocument = .{};
var selected_node: ?usize = null;
var dirty: bool = false;

/// Studio-preview event registry for the "Show UI overlay" toggle (M2.9) —
/// lets an author see a button fire while editing, without a live game/Frame
/// running. The shipped game (M3) wires the exact same `ui_render.drawTree`
/// + `dispatchClicks` calls to its own Frame-driven `UiEvents` instance
/// instead; only the events *source* differs.
var preview_events: ui.UiEvents = .init();
var resolved_ids: []?ui.EventId = &.{};

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

/// Path of the document currently open for editing (empty if none) — lets
/// the scene overlay (C3) substitute the live in-memory copy for the
/// on-disk version of the same document.
pub fn loadedPathPublic() []const u8 {
    return loadedPath();
}

/// The document currently open for editing, if any — consumed by
/// `SceneViewport`'s "Show UI overlay" toggle (D9: same data, same
/// `ui_render.drawTree` call as the shipped game — WYSIWYG by construction).
pub fn currentDocument() ?*const ui.UiDocument {
    if (loaded_path_len == 0) return null;
    return &doc;
}

/// The studio-preview `UiEvents` registry paired with `currentDocument()`.
pub fn currentEvents() *ui.UiEvents {
    return &preview_events;
}

/// Load-time-resolved `EventId`s parallel to `currentDocument().?.nodes`
/// (see `UiEvents.resolveDocument`) — pass straight to `ui_render.dispatchClicks`.
pub fn currentResolvedIds() []const ?ui.EventId {
    return resolved_ids;
}

fn setLoadedPath(path: []const u8) void {
    const n = @min(path.len, loaded_path_buf.len);
    @memcpy(loaded_path_buf[0..n], path[0..n]);
    loaded_path_len = n;
}

/// Loads `path` if it isn't already the loaded document. Idempotent and
/// cheap to call from every panel that touches the document each frame
/// (`drawHierarchyPanel`, `drawViewPanel`, `drawInspector`) — whichever
/// draws first in a frame does the actual load.
fn ensureLoaded(path: []const u8) void {
    if (!std.mem.eql(u8, path, loadedPath())) load(path);
}

fn load(path: []const u8) void {
    _ = arena.reset(.free_all);
    doc = ui.UiDocument.load(arena.allocator(), gui.io, path) catch .{};
    setLoadedPath(path);
    selected_node = if (doc.nodes.len > 0) 0 else null;
    dirty = false;
    preview_events = .init();
    // Studio's authoring preview has no live game pre-registering event
    // types (unlike the real M3 runtime), so every named binding encountered
    // is auto-interned here first — an author can see a button fire while
    // editing without writing a single line of script code. The real
    // runtime's `resolveDocument` stays strict (unresolved -> warn + null).
    for (doc.nodes) |node| {
        for (node.components) |c| {
            if (c != .button) continue;
            switch (c.button.on_click) {
                .named => |name| if (name.len != 0) {
                    _ = preview_events.registerName(name);
                },
            }
        }
    }
    resolved_ids = preview_events.resolveDocument(arena.allocator(), &doc) catch &.{};
}

/// Re-resolve after a structural edit (add/remove node or component) so
/// `currentResolvedIds()` stays in sync with `doc.nodes`. Cheap: editor-only,
/// not the per-frame runtime dispatch path.
fn reresolveEvents() void {
    for (doc.nodes) |node| {
        for (node.components) |c| {
            if (c != .button) continue;
            switch (c.button.on_click) {
                .named => |name| if (name.len != 0) {
                    _ = preview_events.registerName(name);
                },
            }
        }
    }
    resolved_ids = preview_events.resolveDocument(arena.allocator(), &doc) catch resolved_ids;
}

fn save() void {
    const path = loadedPath();
    doc.save(gui.io, path) catch return;
    dirty = false;

    // Keep the cached artifact in sync with the freshly written source
    // (mirrors MaterialEditor/InputActionsEditor's post-save reimport).
    if (EditorState.project_path) |proj| {
        editor.asset_importer.importAssetForce(gui.io, gui.currentWindow().arena(), proj, path);
    }
}

fn newGuid(buf: *[36]u8) []const u8 {
    return editor.Guid.v4(gui.io).toString(buf);
}

// ── Structural mutation commands (applied after the draw loop) ─────────────

const ComponentKind = enum { image, text, layout, button };
const Template = enum { panel, label, image, button };

var cmd_add_node: ?i32 = null;
var cmd_remove_node: ?usize = null;
var cmd_add_component: ?struct { node: usize, kind: ComponentKind } = null;
var cmd_remove_component: ?struct { node: usize, comp: usize } = null;
var cmd_add_template: ?struct { parent: i32, template: Template } = null;

fn defaultComponent(kind: ComponentKind) ui.UiComponent {
    return switch (kind) {
        .image => .{ .image = .{} },
        .text => .{ .text = .{} },
        .layout => .{ .layout = .{} },
        .button => .{ .button = .{} },
    };
}

fn appendNode(parent: i32, name: []const u8, components: []const ui.UiComponent) usize {
    const a = arena.allocator();
    var guid_buf: [36]u8 = undefined;
    const node = ui.UiNode{
        .guid = a.dupe(u8, newGuid(&guid_buf)) catch "",
        .name = a.dupe(u8, name) catch "",
        .parent = parent,
        .components = a.dupe(ui.UiComponent, components) catch &.{},
    };
    var new_nodes = a.alloc(ui.UiNode, doc.nodes.len + 1) catch return 0;
    @memcpy(new_nodes[0..doc.nodes.len], doc.nodes);
    new_nodes[doc.nodes.len] = node;
    doc.nodes = new_nodes;
    dirty = true;
    return doc.nodes.len - 1;
}

/// Removes `index` and every descendant, remapping remaining parent indices.
fn removeNodeAndDescendants(index: usize) void {
    const a = arena.allocator();
    var drop = a.alloc(bool, doc.nodes.len) catch return;
    @memset(drop, false);
    drop[index] = true;
    // Fixed-point pass: mark any node whose parent is already marked. Nodes
    // are usually parented to earlier indices, but this is order-independent.
    var changed = true;
    while (changed) {
        changed = false;
        for (doc.nodes, 0..) |n, i| {
            if (drop[i]) continue;
            if (n.parent >= 0 and drop[@intCast(n.parent)]) {
                drop[i] = true;
                changed = true;
            }
        }
    }

    var remap = a.alloc(i32, doc.nodes.len) catch return;
    var new_count: usize = 0;
    for (drop, 0..) |d, i| {
        remap[i] = if (d) -2 else @intCast(new_count);
        if (!d) new_count += 1;
    }

    var new_nodes = a.alloc(ui.UiNode, new_count) catch return;
    var w: usize = 0;
    for (doc.nodes, 0..) |n, i| {
        if (drop[i]) continue;
        var nn = n;
        nn.parent = if (n.parent < 0) -1 else remap[@intCast(n.parent)];
        new_nodes[w] = nn;
        w += 1;
    }
    doc.nodes = new_nodes;
    selected_node = null;
    dirty = true;
}

fn addComponentTo(node_index: usize, kind: ComponentKind) void {
    const a = arena.allocator();
    const node = &doc.nodes[node_index];
    var new_comps = a.alloc(ui.UiComponent, node.components.len + 1) catch return;
    @memcpy(new_comps[0..node.components.len], node.components);
    new_comps[node.components.len] = defaultComponent(kind);
    node.components = new_comps;
    dirty = true;
}

fn removeComponentFrom(node_index: usize, comp_index: usize) void {
    const a = arena.allocator();
    const node = &doc.nodes[node_index];
    if (comp_index >= node.components.len) return;
    var new_comps = a.alloc(ui.UiComponent, node.components.len - 1) catch return;
    var w: usize = 0;
    for (node.components, 0..) |c, i| {
        if (i == comp_index) continue;
        new_comps[w] = c;
        w += 1;
    }
    node.components = new_comps;
    dirty = true;
}

/// Control templates (D8): factory functions producing ordinary node
/// subtrees, not new primitives — reference implementations for how controls
/// compose. MVP: Panel, Label, Image, Button.
fn addTemplate(parent: i32, template: Template) void {
    switch (template) {
        .panel => _ = appendNode(parent, "Panel", &.{
            .{ .layout = .{ .mode = .column, .padding = .{ 8, 8, 8, 8 } } },
        }),
        .label => _ = appendNode(parent, "Label", &.{
            .{ .text = .{ .text = "Label" } },
        }),
        .image => _ = appendNode(parent, "Image", &.{
            .{ .image = .{} },
        }),
        .button => _ = appendNode(parent, "Button", &.{
            .{ .image = .{} },
            .{ .text = .{ .text = "Button", .text_align = .center } },
            .{ .button = .{} },
        }),
    }
}

fn applyCommands() void {
    const any_command = cmd_add_node != null or cmd_add_template != null or
        cmd_remove_node != null or cmd_add_component != null or cmd_remove_component != null;

    if (cmd_add_node) |parent| {
        selected_node = appendNode(parent, "Node", &.{});
        cmd_add_node = null;
    }
    if (cmd_add_template) |t| {
        selected_node = null;
        addTemplate(t.parent, t.template);
        cmd_add_template = null;
    }
    if (cmd_remove_node) |i| {
        removeNodeAndDescendants(i);
        cmd_remove_node = null;
    }
    if (cmd_add_component) |c| {
        addComponentTo(c.node, c.kind);
        cmd_add_component = null;
    }
    if (cmd_remove_component) |c| {
        removeComponentFrom(c.node, c.comp);
        cmd_remove_component = null;
    }
    // A toolbar/context-menu action is interaction with THIS panel — reclaim
    // the Inspector's selection target in case a Asset Browser peek moved it
    // away since the tab was last activated (mirrors the reclaim in
    // `UiDocModel.select`).
    if (any_command) EditorState.selectAsset(loadedPath());
    if (dirty) reresolveEvents();
}

// ── Panel 1: UI Hierarchy (replaces Scene Hierarchy when a .uidoc tab is
// active — wired in Window.zig) ─────────────────────────────────────────────

/// Header + toolbar (Save/+Node/Add Control) + node tree. The sole place
/// structural commands are recorded AND applied each frame — `drawViewPanel`
/// and `drawInspector` only read `doc`/`selected_node`.
pub fn drawHierarchyPanel(asset_path: []const u8) void {
    ensureLoaded(asset_path);

    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    {
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();
        gui.label(@src(), "UI Hierarchy", .{}, .{ .font = .theme(.heading) });
    }

    drawToolbar();
    _ = gui.separator(@src(), .{ .expand = .horizontal });

    {
        var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both, .min_size_content = .{ .h = 0 }, .max_size_content = .height(0) });
        defer scroll.deinit();
        Tree.draw(outer.data());
    }

    applyCommands();
}

// ── Panel 2: UI View (replaces Scene Viewport when a .uidoc tab is active) ──

/// Live canvas: the exact `ui_render.drawTree`/`fit` call the shipped game
/// and the scene overlay (C3) use, so this is WYSIWYG by construction (D9).
/// Its "gizmo" is a selection-highlight rectangle around the selected node,
/// looked up by GUID tag — the same stable id `ui_render` tags every node
/// with for D6/dvui-testing purposes.
pub fn drawViewPanel(asset_path: []const u8) void {
    ensureLoaded(asset_path);

    var vp = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer vp.deinit();

    {
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(4),
        });
        defer header.deinit();
        gui.label(@src(), "UI View", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });
    }

    var content = gui.box(@src(), .{}, .{ .expand = .both });
    defer content.deinit();

    var stack = gui.overlay(@src(), .{ .expand = .both });
    defer stack.deinit();

    const nat_rect = content.wd.rect;
    const lb = ui_render.fit(.{ .w = nat_rect.w, .h = nat_rect.h }, &doc);
    const result = ui_render.drawTree(&doc, lb, .{ .texture_source = resolveTextureBytes });
    for (result.clicked()) |node_index| toastButtonClick(node_index);
    ui_render.dispatchClicks(result, resolved_ids, &preview_events);

    drawSelectionGizmo();
}

/// Resolves a texture GUID to file bytes for `ui_render`'s image content,
/// reading into the current dvui frame's arena (freed automatically next
/// frame — matches dvui's own per-frame allocation convention).
fn resolveTextureBytes(ctx: ?*anyopaque, guid: []const u8) ?[]const u8 {
    _ = ctx;
    const path = EditorState.resolveAssetGuid(guid) orelse return null;
    return std.Io.Dir.cwd().readFileAlloc(gui.io, path, gui.currentWindow().arena(), .unlimited) catch null;
}

/// Authoring feedback: a clicked button has no live game/Frame to visibly
/// react in edit mode, so surface the `on_click` binding it would fire as a
/// toast — independent of whether the name actually resolved.
fn toastButtonClick(node_index: usize) void {
    if (node_index >= doc.nodes.len) return;
    const node = doc.nodes[node_index];
    var name: []const u8 = "(no on_click binding)";
    for (node.components) |c| {
        if (c != .button) continue;
        switch (c.button.on_click) {
            .named => |n| if (n.len != 0) {
                name = n;
            },
        }
    }
    var buf: [160]u8 = undefined;
    const msg = std.fmt.bufPrint(&buf, "UI click: {s} -> {s}", .{
        if (node.name.len != 0) node.name else "(node)",
        name,
    }) catch return;
    gui.toast(@src(), .{ .message = msg });
}

/// Selection "gizmo" (v1: a highlight rectangle, no drag handles yet — see
/// D3/D6 for why manipulation would key off `LayoutItem.rect` + node GUID).
/// Looks up the selected node's dvui-tagged rect directly rather than
/// threading rects back out of `ui_render`, keeping that module's return
/// contract (`DrawResult` = clicked button indices only) unchanged.
fn drawSelectionGizmo() void {
    const idx = selected_node orelse return;
    if (idx >= doc.nodes.len) return;
    const guid = doc.nodes[idx].guid;
    if (guid.len == 0) return;
    const tag = gui.tagGet(guid) orelse return;
    if (!tag.visible) return;
    tag.rect.stroke(.all(2), .{ .thickness = 2, .color = .{ .r = 90, .g = 165, .b = 245, .a = 255 }, .closed = true });
}

// ── Panel 3: Inspector body (wired from Inspector.zig when a .uidoc tab is
// active) ────────────────────────────────────────────────────────────────────

/// Selected node's properties, or the document's global settings when
/// nothing is selected — mirrors how every other asset type's Inspector
/// falls back to asset-level fields with nothing more specific selected.
pub fn drawInspector(asset_path: []const u8) void {
    ensureLoaded(asset_path);

    var scroll = gui.scrollArea(@src(), .{}, .{
        .expand = .both,
        .min_size_content = .{ .h = 0 },
        .max_size_content = .height(0),
    });
    defer scroll.deinit();

    if (selected_node != null) {
        drawSelectedNode();
    } else {
        drawGlobalSettingsBody();
    }
}

/// The `EditorRegistry`-registered per-asset-type editor (#40): shown when a
/// `.uidoc` is merely *selected* in the Asset Browser, not opened as a tab —
/// document-level fields only (`reference_size`, `scale_mode`), same weight
/// as any other asset type's single-click Inspector view.
pub fn drawGlobalSettings(asset_path: []const u8) void {
    ensureLoaded(asset_path);
    drawGlobalSettingsBody();
}

fn drawGlobalSettingsBody() void {
    if (gui.button(@src(), if (dirty) "Save*" else "Save", .{}, .{ .gravity_y = 0.5 })) save();

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 20 });
    var al = gui.Alignment.init(@src(), 21);
    defer al.deinit();
    var ctx = PropDraw.DrawCtx{ .al = &al, .allocator = arena.allocator() };
    if (PropDraw.drawValue([2]f32, "reference_size", &doc.reference_size, .{}, &ctx, 21)) dirty = true;
    if (PropDraw.drawValue(ui.ScaleMode, "scale_mode", &doc.scale_mode, .{}, &ctx, 22)) dirty = true;
}

var add_control_open: bool = false;

fn drawToolbar() void {
    var toolbar = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer toolbar.deinit();

    if (gui.button(@src(), if (dirty) "Save*" else "Save", .{}, .{ .gravity_y = 0.5 })) save();

    if (gui.button(@src(), "+ Node", .{}, .{ .gravity_y = 0.5, .id_extra = 1 })) {
        cmd_add_node = if (selected_node) |s| @intCast(s) else -1;
    }

    if (gui.button(@src(), "Add Control \u{25b8}", .{}, .{ .gravity_y = 0.5, .id_extra = 3 })) {
        add_control_open = true;
    }

    if (add_control_open) {
        var fw = gui.floatingMenu(@src(), .{ .from = toolbar.data().rectScale().r.toNatural() }, .{ .id_extra = 2 });
        defer fw.deinit();
        drawTemplateMenu(fw);
        if (gui.minSizeGet(fw.data().id) != null and fw.data().id != gui.focusedSubwindowId()) {
            add_control_open = false;
        }
    }
}

fn drawTemplateMenu(fw: *gui.FloatingMenuWidget) void {
    const parent: i32 = if (selected_node) |s| @intCast(s) else -1;
    inline for (.{ "Panel", "Label", "Image", "Button" }, 0..) |label, i| {
        if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = i }) != null) {
            fw.close();
            add_control_open = false;
            cmd_add_template = .{ .parent = parent, .template = @enumFromInt(i) };
        }
    }
}

/// `TreeView` model over the loaded document's flat parent-indexed nodes
/// (C1): rename, context menus and drag-reparenting run through the exact
/// same code path as the scene hierarchy.
const UiDocModel = struct {
    pub fn count() usize {
        return doc.nodes.len;
    }

    pub fn parentOf(i: usize) i32 {
        return doc.nodes[i].parent;
    }

    pub fn name(i: usize) []const u8 {
        return if (doc.nodes[i].name.len > 0) doc.nodes[i].name else "(node)";
    }

    pub fn isSelected(i: usize) bool {
        return selected_node != null and selected_node.? == i;
    }

    pub fn isPrimary(i: usize) bool {
        return isSelected(i);
    }

    pub fn primarySelection() ?usize {
        return selected_node;
    }

    pub fn select(i: usize, mods: tree_view.Mods) void {
        _ = mods; // no multi-select in the UI hierarchy (yet)
        selected_node = i;
        // Reclaim the Inspector's selection target in case the user peeked a
        // different asset in the Asset Browser since this tab was activated
        // (Inspector.zig only shows this document's node view while
        // `selected_asset_path` still points at it).
        EditorState.selectAsset(loadedPath());
    }

    pub fn applyRename(i: usize, new_name: []const u8) void {
        if (i >= doc.nodes.len) return;
        doc.nodes[i].name = arena.allocator().dupe(u8, new_name) catch return;
        dirty = true;
    }

    pub fn reparent(drag: usize, target: usize, zone: tree_view.DropZone) void {
        moveNode(drag, target, zone);
    }

    pub fn removeRequested() void {
        if (selected_node) |s| cmd_remove_node = s;
    }

    pub fn rowIcon(i: usize, has_children: bool) tree_view.RowIcon {
        _ = i;
        return .{ .bytes = if (has_children) gui.entypo.folder else gui.entypo.text_document };
    }

    pub fn contextItems(idx: usize, fw: *gui.FloatingMenuWidget) void {
        if (gui.menuItemLabel(@src(), "Add Child Node", .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            fw.close();
            cmd_add_node = @intCast(idx);
        }
        // Control templates (D8), instantiable as children of this node.
        inline for (.{ "Add Panel", "Add Label", "Add Image", "Add Button" }, 0..) |label, ti| {
            if (gui.menuItemLabel(@src(), label, .{}, .{ .expand = .horizontal, .id_extra = idx * 8 + ti }) != null) {
                fw.close();
                cmd_add_template = .{ .parent = @intCast(idx), .template = @enumFromInt(ti) };
            }
        }
    }
};

const Tree = tree_view.TreeView(UiDocModel);

/// Move `drag` next to / into `target` (C1 drag-reparent). The nodes array
/// is flat and parent-indexed, and sibling render order follows array order —
/// so a move is: reposition the node in the array, then remap every parent
/// index through the old->new index map.
fn moveNode(drag: usize, target: usize, zone: tree_view.DropZone) void {
    const n = doc.nodes.len;
    if (drag >= n or target >= n or drag == target) return;
    // Refuse drops into the drag node's own subtree (the TreeView skips them
    // for the drop indicator, but be defensive at the mutation site too).
    if (Tree.isAncestorOrSelf(target, drag)) return;

    const a = arena.allocator();
    const order = a.alloc(usize, n) catch return;
    var w: usize = 0;
    for (0..n) |i| {
        if (i == drag) continue;
        order[w] = i;
        w += 1;
    }

    var tpos: usize = 0;
    for (order[0..w], 0..) |oi, p| {
        if (oi == target) {
            tpos = p;
            break;
        }
    }
    const insert_at = switch (zone) {
        .before => tpos,
        .into, .after => tpos + 1,
    };
    var i: usize = w;
    while (i > insert_at) : (i -= 1) order[i] = order[i - 1];
    order[insert_at] = drag;

    const remap = a.alloc(i32, n) catch return;
    for (order[0..n], 0..) |oi, ni| remap[oi] = @intCast(ni);

    const new_parent_old: i32 = switch (zone) {
        .into => @intCast(target),
        .before, .after => doc.nodes[target].parent,
    };

    const new_nodes = a.alloc(ui.UiNode, n) catch return;
    for (order[0..n], 0..) |oi, ni| {
        var nn = doc.nodes[oi];
        const p: i32 = if (oi == drag) new_parent_old else nn.parent;
        nn.parent = if (p < 0) -1 else remap[@intCast(p)];
        new_nodes[ni] = nn;
    }
    doc.nodes = new_nodes;

    if (selected_node) |s| selected_node = @intCast(remap[s]);
    dirty = true;
}

/// Precondition: `selected_node != null` (the only caller, `drawInspector`,
/// checks this and falls back to `drawGlobalSettingsBody` otherwise). Draws
/// straight into the caller's scroll area rather than opening its own —
/// nesting two `.expand = .both` scroll areas is a dvui layout error.
fn drawSelectedNode() void {
    const index = selected_node.?;
    if (index >= doc.nodes.len) {
        selected_node = null;
        return;
    }

    const node = &doc.nodes[index];

    {
        var al0 = gui.Alignment.init(@src(), 9);
        defer al0.deinit();
        var ctx0 = PropDraw.DrawCtx{ .al = &al0, .allocator = arena.allocator() };
        if (PropDraw.drawStringEdit("Name", &node.name, .{}, &ctx0, 9)) dirty = true;
    }

    if (gui.checkbox(@src(), &node.active, "Active", .{})) dirty = true;
    gui.label(@src(), "Parent index: {d}", .{node.parent}, .{});

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 10 });
    gui.label(@src(), "Layout item", .{}, .{ .font = .theme(.heading) });
    var al1 = gui.Alignment.init(@src(), 11);
    defer al1.deinit();
    var ctx = PropDraw.DrawCtx{ .al = &al1, .allocator = arena.allocator() };
    if (PropDraw.drawValue(ui.LayoutItem, "item", &node.item, .{}, &ctx, 11)) dirty = true;

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 12 });
    gui.label(@src(), "Style", .{}, .{ .font = .theme(.heading) });
    var al2 = gui.Alignment.init(@src(), 13);
    defer al2.deinit();
    var ctx2 = PropDraw.DrawCtx{ .al = &al2, .allocator = arena.allocator() };
    if (PropDraw.drawValue(ui.StyleBlock, "style", &node.style, .{}, &ctx2, 13)) dirty = true;

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 14 });
    gui.label(@src(), "Components", .{}, .{ .font = .theme(.heading) });
    for (node.components, 0..) |*c, ci| {
        drawComponentRow(index, c, ci);
    }
    drawAddComponentMenu(index);
}

fn drawComponentRow(node_index: usize, c: *ui.UiComponent, ci: usize) void {
    var box = gui.box(@src(), .{}, .{
        .expand = .horizontal,
        .background = true,
        .style = .content,
        .margin = .{ .y = 2 },
        .padding = .all(4),
        .id_extra = ci,
    });
    defer box.deinit();

    {
        var head = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = ci });
        defer head.deinit();
        gui.label(@src(), "{s}", .{@tagName(c.*)}, .{ .expand = .horizontal, .gravity_y = 0.5, .font = .theme(.heading) });
        if (gui.button(@src(), "Remove", .{}, .{ .gravity_y = 0.5, .id_extra = ci })) {
            cmd_remove_component = .{ .node = node_index, .comp = ci };
        }
    }

    // Uniform reflection dispatch (C2): every variant's payload struct is
    // drawn by PropDraw — a new field on any `UiComponent` variant appears
    // here with zero changes to this file.
    switch (c.*) {
        inline else => |*payload| {
            if (PropDraw.drawComponentAlloc(
                @TypeOf(payload.*),
                payload,
                2000 + ci * 16,
                false,
                arena.allocator(),
            )) dirty = true;
        },
    }
}

fn drawAddComponentMenu(node_index: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9000 });
    defer row.deinit();
    inline for (.{ "+Image", "+Text", "+Layout", "+Button" }, 0..) |label, i| {
        if (gui.button(@src(), label, .{}, .{ .id_extra = i })) {
            cmd_add_component = .{ .node = node_index, .kind = @enumFromInt(i) };
        }
    }
    if (gui.button(@src(), "Delete Node", .{}, .{ .id_extra = 4, .style = .err })) {
        cmd_remove_node = node_index;
    }
}
