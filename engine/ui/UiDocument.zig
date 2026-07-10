//! UI document asset (`.uidoc`) — the UI-Toolkit-shaped analogue of a
//! `VisualTreeAsset`: a flat, parent-indexed tree of generic `UiNode`s
//! serialized as JSON. Instantiated into a scene via the `ui_document`
//! `Component` (`TypedAssetRef(.ui_document)`); UI nodes are NOT `SceneNode`s
//! and never touch `engine/scene/Transform.zig`
//! (see `docs/decisions/PLAN_GUI_IMPLEMENTATION.md` D1).
//!
//! Full component model (D2): a node is always an implicit container
//! (`gui.box`); behavior is acquired through `UiComponent`s. v1 ships
//! `image`/`text`/`layout`/`button`. The composition rule for the draw walk
//! (interaction wraps -> box(layout) -> content in declaration order ->
//! children) lives in `subsystems/ui_render/` (M2), not here — this module is
//! pure data + validation, zero dvui imports (D7).
//!
//! Ownership: a `UiDocument` produced by `load`/`loadFromBytes` owns its
//! slices via the parse allocator; release them with `deinit`. Values
//! assembled by a caller (e.g. the editor) must NOT be passed to `deinit`.
const std = @import("std");
const serde = @import("serde");
const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;

// ── Layout (D3): container/child split, no RectTransform ───────────────────

/// Child-side layout data, always present on a node. Maps ~1:1 onto dvui
/// `Options` (`expand`, `gravity_x/y`, `margin`, `min_size_content`).
/// Layout-driven is the default; `rect` is the explicit-position opt-out
/// (CSS `position:absolute` analogue, dvui `Options.rect`).
pub const Expand = enum { none, horizontal, vertical, both };

pub const LayoutItem = struct {
    expand: Expand = .none,
    gravity: [2]f32 = .{ 0, 0 },
    margin: [4]f32 = .{ 0, 0, 0, 0 },
    min_size: [2]f32 = .{ 0, 0 },
    /// Explicit position/size override (x, y, w, h). Null = layout-driven.
    rect: ?[4]f32 = null,
};

/// Container-side layout data — configures how a node's children flow.
/// `flex_wrap`/`grid`/`dock` are later additive variants (D3), not v1.
pub const LayoutMode = enum { row, column };

pub const LayoutComponent = struct {
    mode: LayoutMode = .row,
    gap: f32 = 0,
    padding: [4]f32 = .{ 0, 0, 0, 0 },
};

// ── Styling (D5): guarantees now, tech later ────────────────────────────────

/// Mirrors dvui's `Theme.Style.Name` value set (content/control/highlight/
/// err/app1-3) WITHOUT importing dvui (D7) — `subsystems/ui_render/` maps
/// this to the real `gui.Options.Style` enum.
pub const StyleClass = enum { content, control, highlight, err, app1, app2, app3 };

/// All appearance fields, grouped so this block can later be sourced from an
/// external `.uitheme` asset (`style_ref` + overrides) without touching node
/// semantics (D5.1).
pub const StyleBlock = struct {
    style_class: ?StyleClass = null,
    tint: ?[4]f32 = null,
    corner_radius: ?[4]f32 = null,
    /// Selects a dvui theme font style by name (e.g. "heading", "body",
    /// "caption"). Ignored when `font` below is set — a specific Font asset
    /// always wins over the theme name.
    font_style: ?[]const u8 = null,
    /// Direct reference to a Font asset (#109 follow-up), for text that needs
    /// a specific imported typeface rather than the active theme's. Takes
    /// precedence over `font_style` when set. Independent of the future
    /// Theme asset (#104) that will let a whole document set one in bulk —
    /// this is the per-node escape hatch that doesn't need it.
    font: TypedAssetRef(.font) = .{},
    /// Point size for `font`; ignored for `font_style` (theme fonts carry
    /// their own size). Defaults to a readable UI size (see
    /// `subsystems/ui_render`) when `font` is set but this is left unset.
    font_size: ?f32 = null,
};

// ── Events (D4): strings at rest, handles at runtime, types in user code ───

/// Serialized binding is a union from day one so future binding kinds are
/// additive, not schema-breaking. JSON form: `{"named": "play_clicked"}` or
/// `{"channel": "<game_event asset GUID>"}`.
///
/// `channel` is #107 reframed around #41's event-channel DataAsset instead of
/// a Unity-`UnityEvent`-style node+method binding: the button raises a
/// `GameEvent` asset by GUID (`ui_render.dispatchClicks` resolves it through
/// `GameEventRegistry` and calls `raise()`), and any script anywhere
/// subscribes via `frame.gameEvent(ref).?.on(...)` — decoupled, Inspector-wired,
/// no scene-node coupling and no runtime method-name dispatch to build.
pub const EventBinding = union(enum) {
    named: []const u8,
    channel: TypedAssetRef(.game_event),
};

// ── Components (D2) ──────────────────────────────────────────────────────────

pub const TextAlign = enum { start, center, end };

pub const ImageComponent = struct {
    /// Texture asset reference (D2); unset means unbound. Serialized as a
    /// plain GUID string (see `AssetRef.zerdeSerialize`). Being a
    /// `TypedAssetRef` means the Studio inspector renders a drag-drop zone +
    /// filtered picker for it with zero `studio/` code (C2).
    texture: TypedAssetRef(.texture) = .{},
    /// Nine-patch insets (left, top, right, bottom), or null for a plain stretch.
    ninepatch: ?[4]f32 = null,
};

pub const TextComponent = struct {
    text: []const u8 = "",
    text_align: TextAlign = .start,
};

pub const ButtonComponent = struct {
    on_click: EventBinding = .{ .named = "" },
};

/// Closed union, additive over time. Classified by the `ui_render` draw walk
/// into interaction (`button`; later `toggle`/`drag`/`scroll`), layout (max
/// one per node), and content (`image`/`text`) — see D2's composition rule.
pub const UiComponent = union(enum) {
    image: ImageComponent,
    text: TextComponent,
    layout: LayoutComponent,
    button: ButtonComponent,
};

// ── Node ─────────────────────────────────────────────────────────────────────

pub const UiNode = struct {
    /// Stable identifier, hashed into dvui `Options.id_extra` by the draw walk
    /// (D6 hard requirement) so focus/animation state survives node insertion.
    guid: []const u8 = "",
    name: []const u8 = "",
    /// Index into the document's flat `nodes` array; -1 = root (no parent).
    parent: i32 = -1,
    active: bool = true,
    item: LayoutItem = .{},
    style: StyleBlock = .{},
    components: []UiComponent = &.{},
};

// ── Validation (D2) ──────────────────────────────────────────────────────────

pub const WarningKind = enum {
    parent_out_of_range,
    parent_cycle,
    multiple_layout_components,
    multiple_interaction_components,
};

pub const Warning = struct {
    kind: WarningKind,
    /// Index into `nodes` this warning is about.
    node_index: usize,
};

// ── Document ─────────────────────────────────────────────────────────────────

/// How the authored `reference_size` maps onto the actual viewport (C7).
/// Per-element scaling rules are intentionally absent: split content across
/// multiple documents with different modes instead.
pub const ScaleMode = enum {
    /// Aspect-fit rect + uniformly zoom ALL content (fonts, margins, rects)
    /// by the reference->target factor. What a designed game HUD wants.
    letterbox_zoom,
    /// Aspect-fit rect, content lays out at native size. For tool-like,
    /// resizable UI.
    reflow,
    /// Reserved: no scaling, document coords = viewport pixels.
    constant_pixel,
};

pub const UiDocument = struct {
    /// Current document format version. Bump when the layout changes and add
    /// a migration in `migrate` so older assets keep loading.
    pub const CURRENT_VERSION: u32 = 1;

    /// Reference resolution the document is authored at; `ui_render`
    /// letterboxes this into whatever the actual viewport/screen rect is.
    pub const DEFAULT_REFERENCE_SIZE = [2]f32{ 1920, 1080 };

    version: u32 = CURRENT_VERSION,
    reference_size: [2]f32 = DEFAULT_REFERENCE_SIZE,
    scale_mode: ScaleMode = .letterbox_zoom,
    nodes: []UiNode = &.{},

    // ── Load ─────────────────────────────────────────────────────────────────

    /// Parse a document from in-memory `.uidoc` (JSON) bytes. The returned
    /// value owns its slices; free with `deinit`. `bytes` need not be
    /// NUL-terminated.
    pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !UiDocument {
        var doc = try serde.json.fromSlice(UiDocument, allocator, bytes);
        migrate(&doc);
        return doc;
    }

    /// Load a document from a `.uidoc` file. The returned value owns its
    /// slices; free with `deinit`.
    pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !UiDocument {
        var file = try std.Io.Dir.cwd().openFile(io, path, .{});
        defer file.close(io);
        var fbuf: [4096]u8 = undefined;
        var reader = file.reader(io, &fbuf);
        const content = try reader.interface.allocRemaining(allocator, .unlimited);
        defer allocator.free(content);
        return loadFromBytes(allocator, content);
    }

    /// Free slices owned by a document produced via `load`/`loadFromBytes`.
    /// Must not be called on a document assembled by a caller (e.g. the
    /// editor), whose slices point at caller-owned buffers.
    pub fn deinit(self: UiDocument, allocator: std.mem.Allocator) void {
        for (self.nodes) |node| {
            allocator.free(node.guid);
            allocator.free(node.name);
            if (node.style.font_style) |fs| allocator.free(fs);
            for (node.components) |c| switch (c) {
                .image => {},
                .text => |t| allocator.free(t.text),
                .button => |b| switch (b.on_click) {
                    .named => |n| allocator.free(n),
                    // TypedAssetRef is inline POD (fixed-size buf/len) — no
                    // owned heap memory to free.
                    .channel => {},
                },
                .layout => {},
            };
            if (node.components.len != 0) allocator.free(node.components);
        }
        if (self.nodes.len != 0) allocator.free(self.nodes);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// Serialize this document as pretty-printed JSON into `writer`.
    pub fn serialize(self: UiDocument, writer: *std.Io.Writer) !void {
        try serde.json.toWriterWith(writer, self, .{ .pretty = true });
    }

    /// Write this document to `path` as a `.uidoc` JSON file.
    pub fn save(self: UiDocument, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 64]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try self.serialize(&writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }

    // ── Validation (D2) ────────────────────────────────────────────────────

    /// Structural, load-time validation: parent index cycles/out-of-range,
    /// and more-than-one layout/interaction component on a single node.
    /// Never fails the load — collects warnings into a caller-owned slice.
    ///
    /// NOTE: two D2 checks live elsewhere by design, not here: unresolved
    /// event names need the `UiEvents` registry (`engine/ui/UiEvents.zig`,
    /// resolved once at load time by the *caller*, not this module); unknown
    /// enum values (component kind / layout mode) currently hard-fail JSON
    /// parsing rather than being warned-and-skipped, because that requires
    /// bypassing this project's shared `serde.json` convention with a
    /// hand-rolled parser — flagged as a known gap vs. the plan's literal
    /// wording rather than silently implemented differently.
    pub fn validate(self: UiDocument, allocator: std.mem.Allocator) ![]Warning {
        var warnings: std.ArrayList(Warning) = .empty;
        errdefer warnings.deinit(allocator);

        for (self.nodes, 0..) |node, i| {
            if (node.parent != -1) {
                if (node.parent < 0 or @as(usize, @intCast(node.parent)) >= self.nodes.len) {
                    try warnings.append(allocator, .{ .kind = .parent_out_of_range, .node_index = i });
                } else if (hasParentCycle(self.nodes, i)) {
                    try warnings.append(allocator, .{ .kind = .parent_cycle, .node_index = i });
                }
            }

            var layout_count: u32 = 0;
            var interaction_count: u32 = 0;
            for (node.components) |c| switch (c) {
                .layout => layout_count += 1,
                .button => interaction_count += 1,
                else => {},
            };
            if (layout_count > 1) try warnings.append(allocator, .{ .kind = .multiple_layout_components, .node_index = i });
            if (interaction_count > 1) try warnings.append(allocator, .{ .kind = .multiple_interaction_components, .node_index = i });
        }

        return warnings.toOwnedSlice(allocator);
    }
};

/// Walks `start`'s ancestor chain looking for a cycle. Bounded by `nodes.len`
/// steps so a cycle can never spin forever even before it's detected.
fn hasParentCycle(nodes: []const UiNode, start: usize) bool {
    var slow = start;
    var steps: usize = 0;
    while (steps <= nodes.len) : (steps += 1) {
        const p = nodes[slow].parent;
        if (p == -1) return false;
        const pu: usize = @intCast(p);
        if (pu >= nodes.len) return false; // out-of-range, reported separately
        if (pu == start) return true;
        slow = pu;
    }
    return true;
}

/// Upgrade a just-parsed document in place to `CURRENT_VERSION`. New versions
/// add cases here so old assets keep loading. Currently a no-op stamp.
fn migrate(doc: *UiDocument) void {
    if (doc.version < UiDocument.CURRENT_VERSION) doc.version = UiDocument.CURRENT_VERSION;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "empty document round-trips through JSON" {
    const a = std.testing.allocator;
    const empty = "{}";
    var doc = try UiDocument.loadFromBytes(a, empty);
    defer doc.deinit(a);
    try std.testing.expectEqual(UiDocument.CURRENT_VERSION, doc.version);
    try std.testing.expectEqual(UiDocument.DEFAULT_REFERENCE_SIZE, doc.reference_size);
    try std.testing.expectEqual(@as(usize, 0), doc.nodes.len);
}

test "a panel/label/button node tree round-trips values and components" {
    const a = std.testing.allocator;

    var tex: TypedAssetRef(.texture) = .{};
    tex.set("11111111-1111-4111-8111-111111111111");

    var root_components = [_]UiComponent{
        .{ .layout = .{ .mode = .column, .gap = 8 } },
    };
    var button_components = [_]UiComponent{
        .{ .image = .{ .texture = tex, .ninepatch = .{ 4, 4, 4, 4 } } },
        .{ .text = .{ .text = "Play", .text_align = .center } },
        .{ .button = .{ .on_click = .{ .named = "play_clicked" } } },
    };
    var nodes = [_]UiNode{
        .{ .guid = "root", .name = "Root", .parent = -1, .components = &root_components },
        .{ .guid = "btn", .name = "PlayButton", .parent = 0, .components = &button_components },
    };
    const src = UiDocument{ .nodes = &nodes };

    var buf: [1024 * 8]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try src.serialize(&writer);

    var doc = try UiDocument.loadFromBytes(a, writer.buffered());
    defer doc.deinit(a);

    try std.testing.expectEqual(@as(usize, 2), doc.nodes.len);
    try std.testing.expectEqualStrings("Root", doc.nodes[0].name);
    try std.testing.expectEqual(@as(i32, -1), doc.nodes[0].parent);
    try std.testing.expectEqual(LayoutMode.column, doc.nodes[0].components[0].layout.mode);

    try std.testing.expectEqualStrings("PlayButton", doc.nodes[1].name);
    try std.testing.expectEqual(@as(i32, 0), doc.nodes[1].parent);
    try std.testing.expectEqual(@as(usize, 3), doc.nodes[1].components.len);
    try std.testing.expectEqualStrings("11111111-1111-4111-8111-111111111111", doc.nodes[1].components[0].image.texture.slice());
    try std.testing.expectEqualStrings("Play", doc.nodes[1].components[1].text.text);
    try std.testing.expectEqualStrings("play_clicked", doc.nodes[1].components[2].button.on_click.named);

    const warnings = try doc.validate(a);
    defer a.free(warnings);
    try std.testing.expectEqual(@as(usize, 0), warnings.len);
}

test "validate flags out-of-range and cyclic parents" {
    const a = std.testing.allocator;
    var nodes = [_]UiNode{
        .{ .guid = "a", .parent = 5 }, // out of range
        .{ .guid = "b", .parent = 2 },
        .{ .guid = "c", .parent = 1 }, // b <-> c cycle
    };
    const doc = UiDocument{ .nodes = &nodes };

    const warnings = try doc.validate(a);
    defer a.free(warnings);

    var saw_out_of_range = false;
    var saw_cycle = false;
    for (warnings) |w| {
        if (w.kind == .parent_out_of_range and w.node_index == 0) saw_out_of_range = true;
        if (w.kind == .parent_cycle) saw_cycle = true;
    }
    try std.testing.expect(saw_out_of_range);
    try std.testing.expect(saw_cycle);
}

test "validate flags multiple layout and interaction components" {
    const a = std.testing.allocator;
    var components = [_]UiComponent{
        .{ .layout = .{} },
        .{ .layout = .{ .mode = .column } },
        .{ .button = .{} },
        .{ .button = .{} },
    };
    var nodes = [_]UiNode{
        .{ .guid = "n", .components = &components },
    };
    const doc = UiDocument{ .nodes = &nodes };

    const warnings = try doc.validate(a);
    defer a.free(warnings);

    var saw_layout = false;
    var saw_interaction = false;
    for (warnings) |w| {
        if (w.kind == .multiple_layout_components) saw_layout = true;
        if (w.kind == .multiple_interaction_components) saw_interaction = true;
    }
    try std.testing.expect(saw_layout);
    try std.testing.expect(saw_interaction);
}

test "scale_mode round-trips and defaults to letterbox_zoom (C7)" {
    const a = std.testing.allocator;

    var doc_default = try UiDocument.loadFromBytes(a, "{}");
    defer doc_default.deinit(a);
    try std.testing.expectEqual(ScaleMode.letterbox_zoom, doc_default.scale_mode);

    const src = UiDocument{ .scale_mode = .reflow };
    var buf: [1024]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try src.serialize(&writer);
    var doc = try UiDocument.loadFromBytes(a, writer.buffered());
    defer doc.deinit(a);
    try std.testing.expectEqual(ScaleMode.reflow, doc.scale_mode);
}

test "missing fields fall back to compile-time defaults" {
    const a = std.testing.allocator;
    const json =
        \\{"nodes":[{"guid":"only-guid"}]}
    ;
    var doc = try UiDocument.loadFromBytes(a, json);
    defer doc.deinit(a);
    try std.testing.expectEqual(@as(usize, 1), doc.nodes.len);
    try std.testing.expectEqualStrings("only-guid", doc.nodes[0].guid);
    try std.testing.expectEqualStrings("", doc.nodes[0].name);
    try std.testing.expectEqual(@as(i32, -1), doc.nodes[0].parent);
    try std.testing.expectEqual(LayoutItem{}, doc.nodes[0].item);
}
