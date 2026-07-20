//! Document tab strip UI — split out of `Documents.zig` to keep that file to
//! the document *model* (open/close/activate/save). Everything here is pure
//! drawing plus tab-strip-exclusive UI state (paging, drag-reorder, the
//! close-confirmation dialog); it operates entirely through `Documents.zig`'s
//! `pub` API (`docAt`, `count`, `activeIndex`, `activate`, `close`,
//! `requestClose`, `moveTab`, `confirmCloseIndex`, …), never touching its
//! private `docs` array directly.

const std = @import("std");
const gui = @import("gui");
const EditorState = @import("../services/EditorState.zig");
const Documents = @import("Documents.zig");
const StudioLocale = @import("../services/StudioLocale.zig");

/// Index of the tab currently being dragged for reorder, if any.
var g_drag_tab: ?usize = null;
/// Per-frame cache of each tab's physical rect, used for reorder hit-testing.
var tab_rects: [Documents.MAX_DOCS]gui.Rect.Physical = undefined;

/// Index of the leftmost tab currently shown; the strip pages by this when more
/// tabs are open than fit the window (the ‹ › nav buttons adjust it).
var first_tab: usize = 0;
/// Per-tab physical width measured last frame, used to decide how many tabs fit.
var tab_w: [Documents.MAX_DOCS]f32 = .{0} ** Documents.MAX_DOCS;

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
    while (k < Documents.count()) : (k += 1) {
        used += widthOf(k);
        if (used > avail and k > start) return k;
    }
    return Documents.count();
}

/// Draw the document tab strip. `mouse_held` is whether the left mouse button is
/// currently down (used to drive drag-reordering). The strip keeps a fixed
/// height even with no tabs open, so the editor below it never shifts.
pub fn drawTabBar(mouse_held: bool) void {
    Documents.syncActiveDirty();

    const doc_count = Documents.count();
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

    const active = Documents.activeIndex();
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
            Documents.moveTab(di, di + 1);
            g_drag_tab = di + 1;
        } else if (di > first_tab and mx < tab_rects[di - 1].x + tab_rects[di - 1].w / 2) {
            Documents.moveTab(di, di - 1);
            g_drag_tab = di - 1;
        }
    };

    // Apply activation only if it wasn't actually a close-button click.
    if (to_activate) |ci| {
        if (to_close == null) Documents.activate(ci);
    }
    if (to_close) |ci| Documents.requestClose(ci);

    // Floating ghost + the save/discard/cancel modal are drawn last so they
    // layer above the strip.
    drawDragGhost(mouse_held);
    drawConfirmClose();
}

/// Draw a single tab. Sets `*to_activate` / `*to_close` for the caller to apply
/// after the strip is laid out.
fn drawTab(i: usize, mouse_held: bool, to_activate: *?usize, to_close: *?usize) void {
    const active = Documents.activeIndex();
    const is_active = (active != null and active.? == i);
    const is_dragged = (g_drag_tab != null and g_drag_tab.? == i and mouse_held);

    var tab = gui.box(@src(), .{ .dir = .horizontal }, .{
        .id_extra = i,
        .background = true,
        // A dragged tab lifts to the highlight style as a drag affordance.
        .style = if (is_active or is_dragged) .highlight else .window,
        .border = .all(1),
        .corners = .{ .tl = .theme(4), .tr = .theme(4), .bl = .square, .br = .square },
        .padding = .{ .x = 8, .y = 4, .w = 4, .h = 4 },
        .margin = .{ .w = 2 },
        .gravity_y = 1.0,
        .min_size_content = .{ .w = 60 },
    });
    defer tab.deinit();

    const rect = tab.data().rectScale().r;
    tab_rects[i] = rect;
    // Record width (incl. the inter-tab margin) for next frame's paging maths.
    tab_w[i] = rect.w + 4 * gui.windowNaturalScale();
    const hovered = rect.contains(gui.currentWindow().mouse_pt);

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
    if (Documents.docAt(i).dirty) {
        gui.label(@src(), "*", .{}, .{
            .id_extra = i,
            .gravity_y = 0.5,
            .padding = .{ .w = 4 },
            .font = .theme(.body),
        });
    }

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "{s}", .{trimTitle(Documents.docAt(i).title(), title_max_cache, &name_buf)}, .{
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
    if (di >= Documents.count()) return;

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
        .corners = .all(4),
        .padding = .all(4),
    });
    defer fw.deinit();

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "{s}", .{trimTitle(Documents.docAt(di).title(), title_max_cache, &name_buf)}, .{ .gravity_y = 0.5 });
}
var g_ghost_rect: gui.Rect = .{ .x = 0, .y = 0, .w = 0, .h = 0 };

/// Modal save/discard/cancel prompt shown when closing a document with unsaved
/// changes.
fn drawConfirmClose() void {
    const i = Documents.confirmCloseIndex() orelse return;
    if (i >= Documents.count()) {
        Documents.cancelConfirmClose();
        return;
    }

    var win = gui.floatingWindow(@src(), .{
        .modal = true,
        .center_on = gui.currentWindow().subwindows.current_rect,
        .window_avoid = .nudge,
    }, .{ .role = .dialog, .min_size_content = .{ .w = 320 } });
    defer win.deinit();

    var open_flag = true;
    win.dragAreaSet(gui.windowHeader(StudioLocale.tr("Unsaved Changes"), "", &open_flag));
    if (!open_flag) {
        Documents.cancelConfirmClose();
        return;
    }

    var name_buf: [256]u8 = undefined;
    gui.label(@src(), "{s}", .{StudioLocale.trArgs("Save changes to \"{title}\" before closing?", &.{
        .{ .name = "title", .value = .{ .text = trimTitle(Documents.docAt(i).name(), 48, &name_buf) } },
    })}, .{ .padding = .all(8) });

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_x = 1.0, .padding = .all(4) });
    defer row.deinit();

    if (gui.button(@src(), StudioLocale.tr("Save"), .{}, .{})) {
        Documents.confirmCloseAndSave(i);
    }
    if (gui.button(@src(), StudioLocale.tr("Don't Save"), .{}, .{ .id_extra = 1 })) {
        Documents.confirmCloseWithoutSaving(i);
    }
    if (gui.button(@src(), StudioLocale.tr("Cancel"), .{}, .{ .id_extra = 2 })) {
        Documents.cancelConfirmClose();
    }
}
