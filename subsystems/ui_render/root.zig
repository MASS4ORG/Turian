//! Single UiDocument -> dvui draw walk, shared by studio viewport overlay
//! and the shipped game (D7). Maps engine data onto dvui calls: interaction
//! wrapper → box(layout) → content → children. Texture resolution is
//! caller-supplied via `TextureSource`, keeping this module asset-system-
//! agnostic. Reference-resolution scaling via `fit` + ScaleWidget.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const ui = engine.ui;

/// Converts an `engine.UiTheme` asset into a real `gui.Theme` — the shared
/// conversion point used by both Studio and the shipped game's boot code.
pub const theme = @import("theme.zig");

// ── Reference-resolution letterboxing ───────────────────────────────────────

pub const Letterbox = struct {
    rect: gui.Rect,
    scale: f32,
};

/// Document-level scale modes (C7): computes the rect + content scale for
/// `doc` inside `target` according to `doc.scale_mode`. Pure math, no dvui
/// context needed — safe to call every frame.
/// - `letterbox_zoom` — aspect-fit rect, content zoomed by reference->target.
/// - `reflow` — aspect-fit rect, content at native size (scale 1).
/// - `constant_pixel` (reserved) — no scaling, document coords = viewport px.
pub fn fit(target: gui.Rect, doc: *const ui.UiDocument) Letterbox {
    return switch (doc.scale_mode) {
        .letterbox_zoom => letterbox(target, doc.reference_size),
        .reflow => .{ .rect = letterbox(target, doc.reference_size).rect, .scale = 1 },
        .constant_pixel => .{ .rect = target, .scale = 1 },
    };
}

/// Uniformly scales `reference_size` to fit inside `target`, centering the
/// result. Pure math, no dvui context needed — safe to call every frame.
pub fn letterbox(target: gui.Rect, reference_size: [2]f32) Letterbox {
    if (reference_size[0] <= 0 or reference_size[1] <= 0 or target.w <= 0 or target.h <= 0)
        return .{ .rect = target, .scale = 1 };
    const s = @min(target.w / reference_size[0], target.h / reference_size[1]);
    const w = reference_size[0] * s;
    const h = reference_size[1] * s;
    return .{
        .rect = .{
            .x = target.x + (target.w - w) / 2,
            .y = target.y + (target.h - h) / 2,
            .w = w,
            .h = h,
        },
        .scale = s,
    };
}

// ── Texture resolution (caller-supplied, mirrors subsystems/render/) ───────

/// Returns the raw encoded image bytes (e.g. PNG) for `guid`, or null if
/// unresolved. `ctx` is passed through unchanged (asset-database handle, …).
pub const TextureSource = *const fn (ctx: ?*anyopaque, guid: []const u8) ?[]const u8;

pub const DrawOptions = struct {
    texture_source: ?TextureSource = null,
    texture_ctx: ?*anyopaque = null,
    /// Resolves a Font asset GUID (`StyleBlock.font`) to raw TTF/OTF bytes —
    /// same shape as `texture_source` (both are "give me this GUID's raw
    /// asset bytes"), so a host wires the same function to both. Unlike a
    /// texture, the resolved bytes get registered with dvui exactly once per
    /// GUID (see `ensureFontRegistered`) rather than re-read every draw call.
    font_source: ?TextureSource = null,
    font_ctx: ?*anyopaque = null,
};

// ── Font resolution (GUID -> registered dvui family) ────────────────────────
// Process-lifetime cache: fonts register once per GUID; shared by all hosts.

const MAX_REGISTERED_FONTS = 32;
var registered_font_guids: [MAX_REGISTERED_FONTS][36]u8 = undefined;
var registered_font_count: usize = 0;

fn fontRegistered(guid: []const u8) bool {
    if (guid.len != 36) return false;
    for (registered_font_guids[0..registered_font_count]) |*g| {
        if (std.mem.eql(u8, g, guid)) return true;
    }
    return false;
}

/// Ensures `guid`'s font is registered with dvui under `guid` as the family
/// name (bytes resolved via `src_fn`, the same contract as `TextureSource`).
/// Returns the family name to build a `gui.Font` with, or null if
/// unresolved, an invalid font, or the registry is at capacity.
fn ensureFontRegistered(guid: []const u8, src_fn: TextureSource, ctx: ?*anyopaque) ?[]const u8 {
    if (guid.len != 36) return null;
    if (fontRegistered(guid)) return guid;
    if (registered_font_count >= MAX_REGISTERED_FONTS) return null;

    const bytes = src_fn(ctx, guid) orelse return null;
    // dvui keeps referencing these bytes for the process lifetime (re-atlas
    // whenever a new size is needed), but `src_fn` typically returns bytes
    // scoped to the current frame's arena (matches `texture_source`'s
    // per-frame-read convention) — duplicate into a permanently-owned buffer
    // before handing ownership to dvui.
    const owned = std.heap.page_allocator.dupe(u8, bytes) catch return null;
    gui.addFont(guid, owned, std.heap.page_allocator) catch {
        std.heap.page_allocator.free(owned);
        return null;
    };

    @memcpy(&registered_font_guids[registered_font_count], guid[0..36]);
    registered_font_count += 1;
    return guid;
}

// ── Interaction results ─────────────────────────────────────────────────────

pub const DrawResult = struct {
    pub const MAX_CLICKED = 32;
    node_indices: [MAX_CLICKED]usize = undefined,
    count: usize = 0,

    fn push(self: *DrawResult, i: usize) void {
        if (self.count < MAX_CLICKED) {
            self.node_indices[self.count] = i;
            self.count += 1;
        }
    }

    /// Node indices whose `button` component was clicked this frame.
    pub fn clicked(self: *const DrawResult) []const usize {
        return self.node_indices[0..self.count];
    }
};

// ── Data -> dvui enum/type mapping ──────────────────────────────────────────

fn dvuiDir(mode: ui.LayoutMode) gui.enums.Direction {
    return switch (mode) {
        .row => .horizontal,
        .column => .vertical,
    };
}

fn dvuiExpand(e: ui.Expand) gui.Options.Expand {
    return switch (e) {
        .none => .none,
        .horizontal => .horizontal,
        .vertical => .vertical,
        .both => .both,
    };
}

fn dvuiStyle(c: ui.StyleClass) gui.Theme.Style.Name {
    return switch (c) {
        .content => .content,
        .control => .control,
        .highlight => .highlight,
        .err => .err,
        .app1 => .app1,
        .app2 => .app2,
        .app3 => .app3,
    };
}

fn dvuiColor(t: [4]f32) gui.Color {
    const toU8 = struct {
        fn f(v: f32) u8 {
            return @intFromFloat(std.math.clamp(v, 0, 1) * 255);
        }
    }.f;
    return .{ .r = toU8(t[0]), .g = toU8(t[1]), .b = toU8(t[2]), .a = toU8(t[3]) };
}

/// Stable per-node dvui `id_extra`, hashed from the node GUID (D6 hard
/// requirement) — never derived from array index, so widget state (focus,
/// animation, press) survives node insertion/reordering.
fn idExtraFor(guid: []const u8) usize {
    if (guid.len == 0) return 0;
    return @truncate(std.hash.Wyhash.hash(0, guid));
}

// ── Component lookup (first-of-kind; D2 "max one layout/interaction") ──────

fn findLayout(components: []const ui.UiComponent) ?ui.LayoutComponent {
    for (components) |c| if (c == .layout) return c.layout;
    return null;
}

fn findButton(components: []const ui.UiComponent) ?ui.ButtonComponent {
    for (components) |c| if (c == .button) return c.button;
    return null;
}

// ── Options building ────────────────────────────────────────────────────────

/// Placement-in-parent (from `LayoutItem`) + appearance (from `StyleBlock`)
/// for a node's outermost widget (the interaction wrapper if present, else
/// the node's own box). All values are in document (reference-resolution)
/// coordinates — the ScaleWidget wrapping the tree applies the C7 zoom.
fn outerOptions(node: *const ui.UiNode, id_extra: usize, draw_opts: DrawOptions) gui.Options {
    const item = node.item;
    var opts: gui.Options = .{
        .id_extra = id_extra,
        // Tagged by GUID so `dvui.testing`/E2E tooling can target a node by
        // its stable authoring id (`dvui.tagGet`/`moveTo`), not pixel guesses.
        .tag = if (node.guid.len != 0) node.guid else null,
        .margin = .{
            .x = item.margin[0],
            .y = item.margin[1],
            .w = item.margin[2],
            .h = item.margin[3],
        },
        .gravity_x = item.gravity[0],
        .gravity_y = item.gravity[1],
        .expand = dvuiExpand(item.expand),
    };
    if (item.min_size[0] != 0 or item.min_size[1] != 0) {
        opts.min_size_content = .{ .w = item.min_size[0], .h = item.min_size[1] };
    }
    if (item.rect) |r| {
        opts.rect = .{ .x = r[0], .y = r[1], .w = r[2], .h = r[3] };
    }

    const style = node.style;
    if (style.style_class) |sc| opts.style = dvuiStyle(sc);
    // `font` (specific asset) wins over `font_style` (theme name).
    // Unresolved GUIDs fall back to the inherited font silently.
    if (style.font.slice().len != 0) {
        if (draw_opts.font_source) |fsrc| {
            if (ensureFontRegistered(style.font.slice(), fsrc, draw_opts.font_ctx)) |family| {
                opts.font = .find(.{ .family = family, .size = style.font_size orelse 24 });
            }
        }
    } else if (style.font_style) |fs| {
        if (std.meta.stringToEnum(gui.Font.ThemeFontName, fs)) |name| {
            opts.font = .theme(name);
        }
    }
    if (style.tint) |t| {
        opts.color_fill = dvuiColor(t);
        opts.background = true;
    }
    if (style.corner_radius) |cr| {
        opts.corners = .{ .tl = .theme(cr[0]), .tr = .theme(cr[1]), .br = .theme(cr[2]), .bl = .theme(cr[3]) };
    }
    return opts;
}

// ── Content (image/text components) ─────────────────────────────────────────

fn drawImageContent(img: *const ui.ImageComponent, id_extra: usize, draw_opts: DrawOptions) void {
    const src_fn = draw_opts.texture_source orelse return;
    const guid = img.texture.slice();
    const bytes = src_fn(draw_opts.texture_ctx, guid) orelse return;

    if (img.ninepatch) |np| {
        var patch = gui.Ninepatch{
            .source = .{ .imageFile = .{ .bytes = bytes, .name = guid } },
            .edge = .{ .x = np[0], .y = np[1], .w = np[2], .h = np[3] },
        };
        var box = gui.box(@src(), .{}, .{
            .expand = .both,
            .background = true,
            .ninepatch_fill = &patch,
            .id_extra = id_extra,
        });
        box.deinit();
    } else {
        _ = gui.image(@src(), .{
            .source = .{ .imageFile = .{ .bytes = bytes, .name = guid } },
        }, .{ .expand = .both, .id_extra = id_extra });
    }
}

fn dvuiTextAlign(a: ui.TextAlign) f32 {
    return switch (a) {
        .start => 0.0,
        .center => 0.5,
        .end => 1.0,
    };
}

/// Font passed in (not inherited from parent widget — dvui has no cascade).
/// `.expand = .horizontal` gives `align_x` a width to align within.
fn drawTextContent(t: ui.TextComponent, id_extra: usize, font: ?gui.Font) void {
    gui.labelNoFmt(@src(), t.text, .{ .align_x = dvuiTextAlign(t.text_align) }, .{
        .id_extra = id_extra,
        .font = font,
        .expand = .horizontal,
    });
}

fn drawContent(components: []const ui.UiComponent, id_extra_base: usize, draw_opts: DrawOptions, font: ?gui.Font) void {
    for (components, 0..) |*c, ci| {
        const id_extra = id_extra_base +% ci +% 1;
        switch (c.*) {
            // The image component is passed by pointer so texture GUID slices
            // handed to dvui point at the document's stable storage, not a
            // stack copy.
            .image => |*img| drawImageContent(img, id_extra, draw_opts),
            .text => |t| drawTextContent(t, id_extra, font),
            .layout, .button => {},
        }
    }
}

// ── Node draw walk ──────────────────────────────────────────────────────────

fn drawBoxContentAndChildren(
    doc: *const ui.UiDocument,
    index: usize,
    node: *const ui.UiNode,
    layout: ?ui.LayoutComponent,
    id_extra: usize,
    draw_opts: DrawOptions,
    font: ?gui.Font,
    result: *DrawResult,
) void {
    const mode = if (layout) |l| l.mode else .row;
    const gap = if (layout) |l| l.gap else 0;
    const padding = if (layout) |l| l.padding else .{ 0, 0, 0, 0 };

    var box = gui.box(@src(), .{ .dir = dvuiDir(mode) }, .{
        .expand = .both,
        .id_extra = id_extra +% 1,
        .padding = .{
            .x = padding[0],
            .y = padding[1],
            .w = padding[2],
            .h = padding[3],
        },
    });
    defer box.deinit();

    drawContent(node.components, id_extra, draw_opts, font);

    var sibling_i: usize = 0;
    for (doc.nodes, 0..) |*child, ci| {
        if (child.parent != @as(i32, @intCast(index))) continue;
        drawNode(doc, ci, gap, mode, sibling_i, draw_opts, result);
        sibling_i += 1;
    }
}

fn leadingGapMargin(mode: ui.LayoutMode, sibling_i: usize, gap: f32) [4]f32 {
    if (sibling_i == 0 or gap == 0) return .{ 0, 0, 0, 0 };
    return switch (mode) {
        .row => .{ gap, 0, 0, 0 },
        .column => .{ 0, gap, 0, 0 },
    };
}

fn drawNode(
    doc: *const ui.UiDocument,
    index: usize,
    parent_gap: f32,
    parent_mode: ui.LayoutMode,
    sibling_i: usize,
    draw_opts: DrawOptions,
    result: *DrawResult,
) void {
    const node = &doc.nodes[index];
    if (!node.active) return;

    const id_extra = idExtraFor(node.guid);
    const layout = findLayout(node.components);
    const button = findButton(node.components);

    var opts = outerOptions(node, id_extra, draw_opts);
    // Gap-as-margin only for flow-positioned siblings: applying it to
    // explicit-`item.rect` nodes silently shrinks their rendered rect.
    const extra_margin = if (node.item.rect == null) leadingGapMargin(parent_mode, sibling_i, parent_gap) else .{ 0, 0, 0, 0 };
    opts.margin = .{
        .x = opts.marginGet().x + extra_margin[0],
        .y = opts.marginGet().y + extra_margin[1],
        .w = opts.marginGet().w + extra_margin[2],
        .h = opts.marginGet().h + extra_margin[3],
    };

    if (button != null) {
        var bw: gui.ButtonWidget = undefined;
        bw.init(@src(), .{}, opts);
        bw.processEvents();
        bw.drawBackground();
        if (bw.clicked()) result.push(index);

        drawBoxContentAndChildren(doc, index, node, layout, id_extra, draw_opts, opts.font, result);

        bw.drawFocus();
        bw.deinit();
    } else {
        // No interaction wrapper: the box(layout) itself carries placement +
        // appearance (D2's composition rule collapses to box-only).
        var box = gui.box(@src(), .{ .dir = dvuiDir(if (layout) |l| l.mode else .row) }, opts.override(.{
            .padding = if (layout) |l| .{
                .x = l.padding[0],
                .y = l.padding[1],
                .w = l.padding[2],
                .h = l.padding[3],
            } else null,
        }));
        defer box.deinit();

        drawContent(node.components, id_extra, draw_opts, opts.font);

        var child_sibling_i: usize = 0;
        const gap = if (layout) |l| l.gap else 0;
        const mode = if (layout) |l| l.mode else .row;
        for (doc.nodes, 0..) |*child, ci| {
            if (child.parent != @as(i32, @intCast(index))) continue;
            drawNode(doc, ci, gap, mode, child_sibling_i, draw_opts, result);
            child_sibling_i += 1;
        }
    }
}

// ── Public entry point ──────────────────────────────────────────────────────

/// Draws `doc`'s node tree into `lb.rect` (see `letterbox`), returning which
/// button nodes were clicked this frame (node indices — resolving those to
/// `UiEvents.EventId` and firing is the caller's job, keeping this module
/// free of any event-name string work).
pub fn drawTree(doc: *const ui.UiDocument, lb: Letterbox, draw_opts: DrawOptions) DrawResult {
    var result: DrawResult = .{};

    var root = gui.box(@src(), .{}, .{
        .rect = lb.rect,
        .expand = .none,
    });
    defer root.deinit();

    // ScaleWidget applies the C7 content zoom uniformly — fonts, margins,
    // min-sizes and explicit rects all live in document coordinates and are
    // scaled together (letterbox_zoom), or pass through at 1.0 (reflow).
    var content_scale = lb.scale;
    var sw = gui.scale(@src(), .{ .scale = &content_scale }, .{ .expand = .both });
    defer sw.deinit();

    for (doc.nodes, 0..) |*node, i| {
        if (node.parent != -1) continue;
        drawNode(doc, i, 0, .row, 0, draw_opts, &result);
    }

    return result;
}

/// Fires every clicked node's `on_click` binding. `.named` uses the
/// per-document `resolved_ids` cache (zero string work at dispatch time);
/// `.channel` resolves the asset GUID through `channels`. Null channels are
/// skipped (e.g. editor preview with no live game).
pub fn dispatchClicks(
    doc: *const ui.UiDocument,
    result: DrawResult,
    resolved_ids: []const ?ui.EventId,
    events: *ui.UiEvents,
    channels: ?*engine.GameEventRegistry,
) void {
    for (result.clicked()) |node_index| {
        if (node_index >= doc.nodes.len) continue;
        for (doc.nodes[node_index].components) |c| {
            if (c != .button) continue;
            switch (c.button.on_click) {
                .named => {
                    if (node_index >= resolved_ids.len) continue;
                    const id = resolved_ids[node_index] orelse continue;
                    events.fireId(id);
                },
                .channel => |ch| {
                    const reg = channels orelse continue;
                    const ge = reg.getOrCreate(ch.slice()) orelse continue;
                    ge.raise();
                },
            }
        }
    }
}

// ── Tests (dvui's headless `.testing` backend — real frames, no GPU/window) ─

test "letterbox centers and preserves aspect ratio" {
    // 1000x500 target vs 1920x1080 reference: height is the binding
    // constraint (500/1080 < 1000/1920), so scale = 500/1080 and the
    // letterbox bars land on the left/right (x centered, y flush).
    const lb = letterbox(.{ .x = 0, .y = 0, .w = 1000, .h = 500 }, .{ 1920, 1080 });
    try std.testing.expectApproxEqAbs(@as(f32, 0.462963), lb.scale, 0.001);
    try std.testing.expect(lb.rect.w <= 1000.001);
    try std.testing.expect(lb.rect.h <= 500.001);
    try std.testing.expectApproxEqAbs(@as(f32, 0), lb.rect.y, 0.01);
    try std.testing.expect(lb.rect.x > 0); // centered horizontally
}

test "fit honors document scale_mode (C7)" {
    const target = gui.Rect{ .x = 0, .y = 0, .w = 960, .h = 540 };

    const zoomed = ui.UiDocument{ .scale_mode = .letterbox_zoom };
    const lbz = fit(target, &zoomed);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), lbz.scale, 0.001);

    // reflow: same aspect-fit rect, but content scale stays 1.
    const reflowed = ui.UiDocument{ .scale_mode = .reflow };
    const lbr = fit(target, &reflowed);
    try std.testing.expectEqual(@as(f32, 1), lbr.scale);
    try std.testing.expectApproxEqAbs(lbz.rect.w, lbr.rect.w, 0.001);
    try std.testing.expectApproxEqAbs(lbz.rect.h, lbr.rect.h, 0.001);

    // constant_pixel: document coords = viewport pixels.
    const pixel = ui.UiDocument{ .scale_mode = .constant_pixel };
    const lbp = fit(target, &pixel);
    try std.testing.expectEqual(@as(f32, 1), lbp.scale);
    try std.testing.expectEqual(target.w, lbp.rect.w);
}

test "letterbox degrades gracefully for degenerate input" {
    const lb = letterbox(.{ .x = 0, .y = 0, .w = 0, .h = 500 }, .{ 1920, 1080 });
    try std.testing.expectEqual(@as(f32, 1), lb.scale);
}

test "empty document draws without error" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    const doc = ui.UiDocument{};
    const Ctx = struct {
        var d: *const ui.UiDocument = undefined;
        fn frame() !gui.App.Result {
            _ = drawTree(d, .{ .rect = .{ .w = 800, .h = 600 }, .scale = 1 }, .{});
            return .ok;
        }
    };
    Ctx.d = &doc;
    try gui.testing.settle(Ctx.frame);
}

test "a panel/label/button node tree renders and reports clicks with stable ids across two frames" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    var root_components = [_]ui.UiComponent{
        .{ .layout = .{ .mode = .column, .gap = 8 } },
    };
    var button_components = [_]ui.UiComponent{
        .{ .text = .{ .text = "Play", .text_align = .center } },
        .{ .button = .{} },
    };
    var nodes = [_]ui.UiNode{
        .{ .guid = "root", .name = "Root", .parent = -1, .components = &root_components },
        .{ .guid = "btn", .name = "PlayButton", .parent = 0, .components = &button_components },
    };
    const doc = ui.UiDocument{ .nodes = &nodes };

    const Ctx = struct {
        var d: *const ui.UiDocument = undefined;
        var last_result: DrawResult = .{};
        fn frame() !gui.App.Result {
            last_result = drawTree(d, .{ .rect = .{ .w = 800, .h = 600 }, .scale = 1 }, .{});
            return .ok;
        }
    };
    Ctx.d = &doc;

    try gui.testing.settle(Ctx.frame);
    try std.testing.expectEqual(@as(usize, 0), Ctx.last_result.clicked().len);

    // Same GUID across frames -> same widget id -> dvui.testing can find it
    // by tag/rect without any index-derived identity (D6).
    try gui.testing.settle(Ctx.frame);
}

test "clicking a button reports its node index" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    var button_components = [_]ui.UiComponent{
        .{ .button = .{} },
    };
    // Large explicit min_size so a click near the box's top-left corner is
    // unambiguously inside the button regardless of ButtonWidget's own
    // padding/margin defaults.
    var nodes = [_]ui.UiNode{
        .{ .guid = "btn", .parent = -1, .item = .{ .min_size = .{ 100, 50 } }, .components = &button_components },
    };
    const doc = ui.UiDocument{ .nodes = &nodes };

    const Ctx = struct {
        var d: *const ui.UiDocument = undefined;
        var last_result: DrawResult = .{};
        fn frame() !gui.App.Result {
            last_result = drawTree(d, .{ .rect = .{ .w = 200, .h = 100 }, .scale = 1 }, .{});
            return .ok;
        }
    };
    Ctx.d = &doc;

    try gui.testing.settle(Ctx.frame);

    // Target by the node's GUID tag (see `outerOptions`), not pixel guesses.
    try gui.testing.moveTo("btn");
    try gui.testing.click(.left);
    _ = try gui.testing.step(Ctx.frame);

    try std.testing.expectEqual(@as(usize, 1), Ctx.last_result.clicked().len);
    try std.testing.expectEqual(@as(usize, 0), Ctx.last_result.clicked()[0]);
}

test "full pipeline: click -> resolved EventId -> fireId (M2.9)" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    const PlayClicked = struct {
        pub const event_name = "play_clicked";
    };

    var button_components = [_]ui.UiComponent{
        .{ .button = .{ .on_click = .{ .named = "play_clicked" } } },
    };
    var nodes = [_]ui.UiNode{
        .{ .guid = "btn", .parent = -1, .item = .{ .min_size = .{ 100, 50 } }, .components = &button_components },
    };
    const doc = ui.UiDocument{ .nodes = &nodes };

    var events = ui.UiEvents.init();
    const Sub = struct {
        var fired = false;
        fn onPlay(_: *u8, _: PlayClicked) void {
            fired = true;
        }
    };
    var dummy_ctx: u8 = 0;
    events.on(PlayClicked, &dummy_ctx, Sub.onPlay);

    const resolved = try events.resolveDocument(std.testing.allocator, &doc);
    defer std.testing.allocator.free(resolved);

    const Ctx = struct {
        var d: *const ui.UiDocument = undefined;
        var last_result: DrawResult = .{};
        fn frame() !gui.App.Result {
            last_result = drawTree(d, .{ .rect = .{ .w = 200, .h = 100 }, .scale = 1 }, .{});
            return .ok;
        }
    };
    Ctx.d = &doc;

    try gui.testing.settle(Ctx.frame);
    try gui.testing.moveTo("btn");
    try gui.testing.click(.left);
    _ = try gui.testing.step(Ctx.frame);

    try std.testing.expect(!Sub.fired);
    dispatchClicks(&doc, Ctx.last_result, resolved, &events, null);
    try std.testing.expect(Sub.fired);
}

test "channel binding raises the shared GameEvent through GameEventRegistry" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    var button_components = [_]ui.UiComponent{
        .{ .button = .{ .on_click = .{ .channel = channel_ref: {
            var r: engine.TypedAssetRef(.game_event) = .{};
            r.set("door-channel-guid");
            break :channel_ref r;
        } } } },
    };
    var nodes = [_]ui.UiNode{
        .{ .guid = "btn", .parent = -1, .item = .{ .min_size = .{ 100, 50 } }, .components = &button_components },
    };
    const doc = ui.UiDocument{ .nodes = &nodes };

    var events = ui.UiEvents.init();
    const resolved = try events.resolveDocument(std.testing.allocator, &doc);
    defer std.testing.allocator.free(resolved);

    var registry = engine.GameEventRegistry.init();
    const Sub = struct {
        var fired = false;
        fn onRaised(_: *u8) void {
            fired = true;
        }
    };
    var dummy_ctx: u8 = 0;
    registry.getOrCreate("door-channel-guid").?.on(&dummy_ctx, Sub.onRaised);

    const Ctx = struct {
        var d: *const ui.UiDocument = undefined;
        var last_result: DrawResult = .{};
        fn frame() !gui.App.Result {
            last_result = drawTree(d, .{ .rect = .{ .w = 200, .h = 100 }, .scale = 1 }, .{});
            return .ok;
        }
    };
    Ctx.d = &doc;

    try gui.testing.settle(Ctx.frame);
    try gui.testing.moveTo("btn");
    try gui.testing.click(.left);
    _ = try gui.testing.step(Ctx.frame);

    try std.testing.expect(!Sub.fired);
    dispatchClicks(&doc, Ctx.last_result, resolved, &events, &registry);
    try std.testing.expect(Sub.fired);
}

test "ninepatch image content renders from raw pixel bytes without erroring (styling spike)" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    // Tiny synthetic 4x4 RGBA checkerboard "texture" — exercises the
    // ImageSource.pixels path directly, which is what a decoded engine
    // texture already is (no re-encode round trip needed).
    const Ctx = struct {
        var pixels: [4 * 4 * 4]u8 = undefined;
        fn frame() !gui.App.Result {
            var box = gui.box(@src(), .{}, .{ .expand = .both, .min_size_content = .{ .w = 64, .h = 64 } });
            defer box.deinit();
            _ = gui.image(@src(), .{
                .source = .{ .pixels = .{ .rgba = &pixels, .width = 4, .height = 4 } },
            }, .{ .expand = .both });
            return .ok;
        }
    };
    for (0..16) |i| {
        const on = (i % 2) == 0;
        Ctx.pixels[i * 4 + 0] = if (on) 255 else 0;
        Ctx.pixels[i * 4 + 1] = if (on) 255 else 0;
        Ctx.pixels[i * 4 + 2] = if (on) 255 else 0;
        Ctx.pixels[i * 4 + 3] = 255;
    }
    try gui.testing.settle(Ctx.frame);
}

test "style_class maps to every dvui theme style without erroring" {
    var t = try gui.testing.init(.{});
    defer t.deinit();

    const classes = [_]ui.StyleClass{ .content, .control, .highlight, .err, .app1, .app2, .app3 };
    const Ctx = struct {
        var i: usize = 0;
        fn frame() !gui.App.Result {
            var box = gui.box(@src(), .{}, .{ .style = dvuiStyle(classes[i]), .background = true, .id_extra = i });
            box.deinit();
            return .ok;
        }
    };
    for (classes, 0..) |_, i| {
        Ctx.i = i;
        try gui.testing.settle(Ctx.frame);
    }
}
