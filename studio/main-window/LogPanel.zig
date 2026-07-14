//! Output/Log panel: a dockable console showing every captured
//! engine/editor log message, filterable by level/category/text, with
//! full-history repeat-collapsing, a double-click-to-open source location,
//! and lazy-symbolized stack traces for error entries.
//!
//! Data source: `engine.DiagLog`'s ring buffer (fed by `std_options.logFn`,
//! see `Main.zig`). This panel only reads a per-frame snapshot into its own
//! module-level buffer — it never touches the ring's internal lock itself
//! beyond that one call.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const ExternalEditor = @import("../services/ExternalEditor.zig");

const DiagLog = engine.DiagLog;
const Entry = DiagLog.Entry;

const level_icons = [4][]const u8{
    gui.entypo.bug, // debug
    gui.entypo.info_with_circle, // info
    gui.entypo.warning, // warn
    gui.entypo.circle_with_cross, // err
};
const level_names = [4][]const u8{ "Debug", "Info", "Warning", "Error" };
const level_colors = [4]gui.Color{
    .{ .r = 0x90, .g = 0x90, .b = 0x90 }, // debug: gray
    .{ .r = 0xd8, .g = 0xd8, .b = 0xd8 }, // info: near-white
    .{ .r = 0xe0, .g = 0xb0, .b = 0x30 }, // warn: amber
    .{ .r = 0xe0, .g = 0x50, .b = 0x50 }, // err: red
};

const MAX_CATEGORIES = 24;
/// Display category every SDL/dvui internal scope collapses into — keeps the
/// category list and message flood from drowning out user/game logs.
const SYSTEM_CATEGORY = "Studio";
/// A row for double-click purposes; two clicks on the same entry within this
/// window count as a double-click.
const DOUBLE_CLICK_NS: i128 = 500 * std.time.ns_per_ms;

/// Whether each level (indexed by `@intFromEnum(Level)`) is currently shown.
var g_show: [4]bool = .{ true, true, true, true };
var g_search_buf: [128]u8 = .{0} ** 128;
/// Selected category name ("" means All), persisted across frames since the
/// category dropdown's index isn't stable as new categories appear. Sized to
/// match `DiagLog`'s scope capacity.
var g_category_buf: [32]u8 = .{0} ** 32;
/// Panel setting (View ▸ Output ▸ "..."): hides `SYSTEM_CATEGORY` noise by
/// default so user-code and user-triggered-action (build/compile) logs lead.
var g_show_system: bool = false;
/// Unity-style "Collapse": merge *all* matching messages (not just
/// consecutive ones) into one row with a total counter.
var g_collapse: bool = false;

var g_auto_scroll: bool = true;
var g_scroll_info: gui.ScrollInfo = .{};
var g_last_newest_seq: u64 = 0;
var g_last_virtual_h: f32 = 0;

/// Snapshot buffer, reused frame to frame (too big to put on the stack).
var g_entries: [DiagLog.capacity]Entry = undefined;
/// Rows to render this frame, after filtering and (optionally) collapsing.
var g_rows: [DiagLog.capacity]Row = undefined;

/// Lazily symbolized stack trace, cached for whichever error entry was last
/// expanded (symbolizing is a real debug-info lookup, not free).
var g_trace_seq: ?u64 = null;
var g_trace_text: []u8 = &.{};

/// For double-click-to-open detection, keyed by `Entry.seq`.
var g_last_click_seq: ?u64 = null;
var g_last_click_ns: i128 = 0;

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn selectedCategory() []const u8 {
    return bufStr(&g_category_buf);
}

/// SDL and dvui log through their own internal scope names (`SDL_SYSTEM`,
/// `SDL3GPUBackend`, `dvui`, ...) — framework noise, not user/game code.
fn isSystemScope(scope: []const u8) bool {
    return std.mem.startsWith(u8, scope, "SDL") or std.mem.startsWith(u8, scope, "dvui");
}

/// The category a message displays/filters under: system scopes collapse
/// into one `SYSTEM_CATEGORY` bucket, everything else keeps its own scope.
fn displayCategory(scope: []const u8) []const u8 {
    return if (isSystemScope(scope)) SYSTEM_CATEGORY else scope;
}

/// Draws the Output panel content. Registered as the `"output"` panel in
/// `Panels.zig`.
pub fn draw() void {
    const n = DiagLog.snapshot(&g_entries);
    const entries = g_entries[0..n];

    var col = gui.box(@src(), .{ .dir = .vertical }, .{ .expand = .both });
    defer col.deinit();

    drawToolbar(entries);
    _ = gui.separator(@src(), .{ .expand = .horizontal });
    drawList(entries);
}

/// Dock header "..." menu content (`PanelDesc.settings`).
pub fn drawSettings(instance_id: []const u8) void {
    _ = instance_id;
    _ = gui.checkbox(@src(), &g_show_system, "Show system messages", .{ .expand = .horizontal });
}

/// Filter row: level toggles, category dropdown, search box, collapse,
/// auto-scroll, copy/clear.
fn drawToolbar(entries: []const Entry) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .padding = gui.Rect.all(4),
    });
    defer row.deinit();

    inline for (level_icons, 0..) |icon, i| {
        if (gui.buttonIcon(@src(), level_names[i], icon, .{}, .{}, .{
            .id_extra = i,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 22, .h = 22 },
            .style = if (g_show[i]) .highlight else .control,
        })) {
            g_show[i] = !g_show[i];
        }
    }

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    drawCategoryDropdown(entries);

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    var te = gui.textEntry(@src(), .{ .text = .{ .buffer = &g_search_buf } }, .{
        .min_size_content = .{ .w = 160 },
        .gravity_y = 0.5,
    });
    te.deinit();

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    if (gui.button(@src(), "Collapse", .{}, .{
        .gravity_y = 0.5,
        .style = if (g_collapse) .highlight else .control,
    })) {
        g_collapse = !g_collapse;
    }

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    _ = gui.checkbox(@src(), &g_auto_scroll, "Auto-scroll", .{ .gravity_y = 0.5 });

    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    if (gui.button(@src(), "Copy", .{}, .{ .gravity_y = 0.5 })) copyAll(entries);
    if (gui.button(@src(), "Clear", .{}, .{ .gravity_y = 0.5 })) DiagLog.reset();
}

/// Builds the category list from the currently snapshotted entries (subject
/// to the system-messages setting, but not the other filters) and draws the
/// "All"/category dropdown, updating `g_category_buf` on change.
fn drawCategoryDropdown(entries: []const Entry) void {
    var cat_buf: [1 + MAX_CATEGORIES][]const u8 = undefined;
    cat_buf[0] = "All";
    var cat_n: usize = 1;
    for (entries) |*e| {
        if (isSystemScope(e.scope()) and !g_show_system) continue;
        const c = displayCategory(e.scope());
        if (c.len == 0) continue;
        var seen = false;
        for (cat_buf[0..cat_n]) |existing| {
            if (std.mem.eql(u8, existing, c)) {
                seen = true;
                break;
            }
        }
        if (!seen and cat_n < cat_buf.len) {
            cat_buf[cat_n] = c;
            cat_n += 1;
        }
    }
    const categories = cat_buf[0..cat_n];

    var selected: usize = 0;
    const current = selectedCategory();
    if (current.len > 0) {
        for (categories, 0..) |c, i| {
            if (std.mem.eql(u8, c, current)) {
                selected = i;
                break;
            }
        }
    }

    const changed = gui.dropdown(@src(), categories, .{ .choice = &selected }, .{}, .{
        .min_size_content = .{ .w = 120 },
        .gravity_y = 0.5,
    });
    if (changed) {
        if (selected == 0) {
            g_category_buf[0] = 0;
        } else {
            const name = categories[selected];
            const l = @min(name.len, g_category_buf.len - 1);
            @memcpy(g_category_buf[0..l], name[0..l]);
            g_category_buf[l] = 0;
        }
    }
}

fn passesFilter(e: *const Entry, search: []const u8, category: []const u8) bool {
    if (!g_show[@intFromEnum(e.level)]) return false;
    if (isSystemScope(e.scope()) and !g_show_system) return false;
    if (category.len > 0 and !std.mem.eql(u8, displayCategory(e.scope()), category)) return false;
    if (search.len > 0 and std.ascii.indexOfIgnoreCase(e.message(), search) == null) return false;
    return true;
}

/// A displayed row: which entry represents it (its most recent occurrence)
/// and the total count backing its "×N" badge.
const Row = struct { entry: *const Entry, total: u32 };

/// Filters `entries` (newest-first) into `out`, optionally collapsing *all*
/// matching (level, category, message) entries into one row with a summed
/// count — not just the consecutive-run collapsing `DiagLog` already does.
fn buildRows(entries: []const Entry, search: []const u8, category: []const u8, out: []Row) usize {
    var n: usize = 0;
    for (entries) |*e| {
        if (!passesFilter(e, search, category)) continue;
        if (g_collapse) {
            var found = false;
            for (out[0..n]) |*row| {
                const o = row.entry;
                if (o.level == e.level and std.mem.eql(u8, o.scope(), e.scope()) and std.mem.eql(u8, o.message(), e.message())) {
                    row.total += e.repeat;
                    found = true;
                    break;
                }
            }
            if (found) continue;
        }
        if (n < out.len) {
            out[n] = .{ .entry = e, .total = e.repeat };
            n += 1;
        }
    }
    return n;
}

/// Scrollable list, oldest at top / newest at bottom (console convention),
/// auto-scrolling to the bottom when new entries arrive.
fn drawList(entries: []const Entry) void {
    const search = bufStr(&g_search_buf);
    const category = selectedCategory();

    const n = buildRows(entries, search, category, &g_rows);
    const rows = g_rows[0..n];

    // Only force the scroll position when something actually changed (new
    // entry, or `virtual_size` catching up to a backlog laid out on a prior
    // frame) — forcing it unconditionally every frame fights the scrollbar,
    // making it impossible to drag manually.
    const newest_seq: u64 = if (rows.len > 0) rows[0].entry.seq else 0;
    const content_changed = newest_seq != g_last_newest_seq or g_scroll_info.virtual_size.h != g_last_virtual_h;
    if (g_auto_scroll and content_changed) g_scroll_info.scrollToFraction(.vertical, 1.0);
    g_last_newest_seq = newest_seq;
    g_last_virtual_h = g_scroll_info.virtual_size.h;

    // `min_size_content`/`max_size_content` of 0 stop the scroll area's own
    // (potentially very tall) content from inflating its parents' natural
    // size — without this, a long log list can squeeze the Studio footer
    // task bar (which lives outside the dockspace entirely) off-window.
    var scroll = gui.scrollArea(@src(), .{ .scroll_info = &g_scroll_info }, .{
        .expand = .both,
        .min_size_content = .{ .h = 0 },
        .max_size_content = .height(0),
    });
    defer scroll.deinit();

    var i = rows.len;
    while (i > 0) {
        i -= 1;
        drawRow(rows[i].entry, rows[i].total);
    }
}

/// One log entry: a timestamp, level icon, category, message, and (if
/// collapsed more than once) a total-count badge. Double-click opens the
/// message's `file:line` reference (if any) in the external editor. Errors
/// with a captured stack trace get an expand arrow to show it.
fn drawRow(e: *const Entry, total: u32) void {
    const row_seq: usize = @intCast(e.seq);
    const lvl = @intFromEnum(e.level);
    const has_trace = e.level == .err and e.trace_len > 0;

    const row_id = gui.parentGet().extendId(@src(), row_seq);
    const expanded = gui.dataGetPtrDefault(null, row_id, "expanded", bool, false);

    var col = gui.box(@src(), .{ .dir = .vertical }, .{ .expand = .horizontal, .id_extra = row_seq });
    defer col.deinit();

    var head = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer head.deinit();

    // No stack trace to reveal: skip the toggle entirely rather than show a
    // dead button, but keep its width so level icons still line up.
    if (has_trace) {
        if (gui.buttonIcon(@src(), "toggle", if (expanded.*) gui.entypo.triangle_down else gui.entypo.triangle_right, .{}, .{}, .{
            .min_size_content = .{ .w = 14, .h = 14 },
            .gravity_y = 0.5,
        })) {
            expanded.* = !expanded.*;
        }
    } else {
        _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 14 } });
    }

    const tod = e.timeOfDay();
    gui.label(@src(), "{d:0>2}:{d:0>2}:{d:0>2}", .{ tod.getHoursIntoDay(), tod.getMinutesIntoHour(), tod.getSecondsIntoMinute() }, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 56 },
        .color_text = level_colors[0],
    });

    gui.icon(@src(), "level", level_icons[lvl], .{}, .{
        .min_size_content = .{ .w = 14, .h = 14 },
        .gravity_y = 0.5,
        .color_text = level_colors[lvl],
    });

    const cat = displayCategory(e.scope());
    if (cat.len > 0) {
        gui.label(@src(), "{s}:", .{cat}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 70 } });
    }

    gui.label(@src(), "{s}", .{e.message()}, .{
        .color_text = level_colors[lvl],
        .gravity_y = 0.5,
        .expand = .horizontal,
    });

    if (editor.log_location.find(e.message())) |loc| {
        var buf: [300]u8 = undefined;
        const hint = std.fmt.bufPrint(&buf, "({s}:{d})", .{ loc.path, loc.line }) catch "";
        gui.label(@src(), "{s}", .{hint}, .{ .gravity_y = 0.5, .color_text = level_colors[0] });
    }

    if (total > 1) {
        gui.label(@src(), "x{d}", .{total}, .{ .gravity_y = 0.5, .style = .highlight });
    }

    handleRowClick(head.data(), e);

    if (expanded.* and has_trace) drawStackTrace(e);
}

/// Double-click (within `DOUBLE_CLICK_NS` on the same entry) opens the
/// message's `file:line` reference, if any, in the external editor.
fn handleRowClick(wd: *gui.WidgetData, e: *const Entry) void {
    for (gui.events()) |*ev| {
        if (!gui.eventMatchSimple(ev, wd)) continue;
        switch (ev.evt) {
            .mouse => |me| {
                if (me.action != .press or me.button != .left) continue;
                ev.handle(@src(), wd);
                const now = gui.frameTimeNS();
                const is_double = g_last_click_seq != null and g_last_click_seq.? == e.seq and now - g_last_click_ns < DOUBLE_CLICK_NS;
                if (is_double) {
                    if (editor.log_location.find(e.message())) |loc| {
                        ExternalEditor.openAtLocation(loc.path, loc.line);
                    }
                    g_last_click_seq = null;
                } else {
                    g_last_click_seq = e.seq;
                    g_last_click_ns = now;
                }
            },
            else => {},
        }
    }
}

/// Draws (and lazily symbolizes/caches) the stack trace for an expanded
/// error entry.
fn drawStackTrace(e: *const Entry) void {
    if (g_trace_seq != e.seq) {
        if (g_trace_text.len > 0) std.heap.page_allocator.free(g_trace_text);
        g_trace_text = DiagLog.symbolizeTrace(std.heap.page_allocator, e) catch
            (std.heap.page_allocator.dupe(u8, "(failed to symbolize)") catch &.{});
        g_trace_seq = e.seq;
    }
    gui.label(@src(), "{s}", .{g_trace_text}, .{
        .expand = .horizontal,
        .margin = .{ .x = 18 },
        .color_text = level_colors[0],
    });
}

/// Copies every currently-visible entry (respecting active filters and
/// Collapse) to the system clipboard, oldest first.
fn copyAll(entries: []const Entry) void {
    const search = bufStr(&g_search_buf);
    const category = selectedCategory();

    var buf: [DiagLog.capacity]Row = undefined;
    const n = buildRows(entries, search, category, &buf);
    const rows = buf[0..n];

    var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
    defer out.deinit();

    var i = rows.len;
    while (i > 0) {
        i -= 1;
        const e = rows[i].entry;
        const total = rows[i].total;
        if (total > 1) {
            out.writer.print("[{s}] {s}: {s} (x{d})\n", .{ level_names[@intFromEnum(e.level)], displayCategory(e.scope()), e.message(), total }) catch {};
        } else {
            out.writer.print("[{s}] {s}: {s}\n", .{ level_names[@intFromEnum(e.level)], displayCategory(e.scope()), e.message() }) catch {};
        }
    }
    gui.clipboardTextSet(out.written());
}
