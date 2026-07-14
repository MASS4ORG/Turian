//! Inspector panel for `.uitheme` assets. Edits every color field of
//! `engine.UiTheme` (base colors + the 7 style groups) plus corner rounding,
//! mirroring `MaterialEditor.zig`'s structure (module-level loaded state,
//! dirty tracking, Save row). Live preview registered separately as this
//! type's `PreviewSystem.LiveDrawFn` (`drawPreview` below).
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const ui_render = @import("ui_render");

const UiTheme = engine.UiTheme;
const Style = UiTheme.Style;
const Color = UiTheme.Color;

const GROUP_NAMES = [_][]const u8{ "Control", "Window", "Highlight", "Error", "App 1 (Panel)", "App 2", "App 3" };

// ── Loaded state ─────────────────────────────────────────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;
var dirty: bool = false;

var name_buf: [128]u8 = .{0} ** 128;
var dark: bool = false;
var focus: Color = .{};
var fill: Color = .{};
var fill_hover: ?Color = null;
var fill_press: ?Color = null;
var text: Color = .{};
var text_hover: ?Color = null;
var text_press: ?Color = null;
var border: Color = .{};
var groups: [7]Style = [_]Style{.{}} ** 7;
var corner_kind: UiTheme.Corner.Kind = .round;
var corner_rx: f32 = 5;
var corner_y: f32 = 0;
var panel_border_width: f32 = 1;
var font_family_buf: [64]u8 = .{0} ** 64;

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn setBuf(dst: []u8, s: []const u8) void {
    const n = @min(s.len, dst.len - 1);
    @memcpy(dst[0..n], s[0..n]);
    @memset(dst[n..], 0);
}

// ── Draw ─────────────────────────────────────────────────────────────────────

pub fn draw(asset_path: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 } });
        defer row.deinit();
        gui.label(@src(), "Name", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 100 } });
        var te = gui.textEntry(@src(), .{ .text = .{ .buffer = &name_buf } }, .{ .gravity_y = 0.5, .expand = .horizontal });
        const changed = te.text_changed;
        te.deinit();
        if (changed) dirty = true;
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = 1 });
        defer row.deinit();
        gui.label(@src(), "Dark", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 100 } });
        const before = dark;
        _ = gui.checkbox(@src(), &dark, "", .{ .gravity_y = 0.5 });
        if (dark != before) dirty = true;
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 100 });
    if (drawColor("Focus", &focus, 101)) dirty = true;
    if (drawColor("Fill", &fill, 102)) dirty = true;
    if (drawOptionalColor("Fill Hover", &fill_hover, fill, 103)) dirty = true;
    if (drawOptionalColor("Fill Press", &fill_press, fill, 104)) dirty = true;
    if (drawColor("Text", &text, 105)) dirty = true;
    if (drawOptionalColor("Text Hover", &text_hover, text, 106)) dirty = true;
    if (drawOptionalColor("Text Press", &text_press, text, 107)) dirty = true;
    if (drawColor("Border", &border, 108)) dirty = true;

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 200 });
    for (&groups, 0..) |*g, gi| {
        if (drawStyleGroup(GROUP_NAMES[gi], g, 300 + gi * 10)) dirty = true;
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 500 });
    if (drawCorner()) dirty = true;
    if (drawPanelBorderWidth()) dirty = true;
    if (drawFontFamily()) dirty = true;

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 600 });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6), .id_extra = 601 });
        defer row.deinit();
        if (dirty)
            gui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal })
        else
            gui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        if (gui.button(@src(), "Save", .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control })) {
            save();
        }
    }
}

fn drawStyleGroup(title: []const u8, s: *Style, id: usize) bool {
    var changed = false;
    if (gui.expander(@src(), title, .{}, .{ .expand = .horizontal, .id_extra = id })) {
        var body = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 12 }, .id_extra = id });
        defer body.deinit();
        if (drawOptionalColor("Fill", &s.fill, fill, id + 1)) changed = true;
        if (drawOptionalColor("Fill Hover", &s.fill_hover, s.fill orelse fill, id + 2)) changed = true;
        if (drawOptionalColor("Fill Press", &s.fill_press, s.fill orelse fill, id + 3)) changed = true;
        if (drawOptionalColor("Text", &s.text, text, id + 4)) changed = true;
        if (drawOptionalColor("Text Hover", &s.text_hover, s.text orelse text, id + 5)) changed = true;
        if (drawOptionalColor("Text Press", &s.text_press, s.text orelse text, id + 6)) changed = true;
        if (drawOptionalColor("Border", &s.border, border, id + 7)) changed = true;
    }
    return changed;
}

fn drawColor(label: []const u8, c: *Color, id: usize) bool {
    var changed = false;
    if (gui.expander(@src(), label, .{}, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = id })) {
        var body = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 12, .y = 2 }, .id_extra = id });
        defer body.deinit();
        var hsv = gui.Color.HSV.fromColor(.{ .r = c.r, .g = c.g, .b = c.b, .a = c.a });
        if (gui.colorPicker(@src(), .{ .hsv = &hsv, .alpha = true, .sliders = .rgb }, .{ .expand = .horizontal, .id_extra = id })) {
            const rc = hsv.toColor();
            c.* = .{ .r = rc.r, .g = rc.g, .b = rc.b, .a = rc.a };
            changed = true;
        }
    }
    return changed;
}

/// Optional color row: a checkbox toggles whether this field overrides
/// `fallback` (its resolved theme value when unset); the picker only shows
/// while overriding.
fn drawOptionalColor(label: []const u8, c: *?Color, fallback: Color, id: usize) bool {
    var changed = false;
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = id });
    var overriding = c.* != null;
    if (gui.checkbox(@src(), &overriding, label, .{ .gravity_y = 0.5, .id_extra = id })) {
        c.* = if (overriding) (c.* orelse fallback) else null;
        changed = true;
    }
    row.deinit();

    if (overriding) {
        var value = c.* orelse fallback;
        if (drawColor(label, &value, id + 5000)) {
            c.* = value;
            changed = true;
        }
    }
    return changed;
}

fn drawCorner() bool {
    var changed = false;
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = 501 });
        defer row.deinit();
        gui.label(@src(), "Corner", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 100 } });
        if (gui.dropdownEnum(@src(), UiTheme.Corner.Kind, .{ .choice = &corner_kind }, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 120 },
            .id_extra = 501,
        })) changed = true;
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = 502 });
        defer row.deinit();
        gui.label(@src(), "Radius", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 100 } });
        if (gui.sliderEntry(@src(), "{d:0.0}", .{ .value = &corner_rx, .min = 0, .max = 24 }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 502 })) changed = true;
    }
    return changed;
}

fn drawPanelBorderWidth() bool {
    var changed = false;
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = 503 });
    defer row.deinit();
    gui.label(@src(), "Panel Border Width", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 130 } });
    if (gui.sliderEntry(@src(), "{d:0.0}", .{ .value = &panel_border_width, .min = 0, .max = 8 }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 503 })) changed = true;
    return changed;
}

fn drawFontFamily() bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = 504 });
    defer row.deinit();
    gui.label(@src(), "Font Family", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 130 } });
    var te = gui.textEntry(@src(), .{ .text = .{ .buffer = &font_family_buf } }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 504 });
    const changed = te.text_changed;
    te.deinit();
    return changed;
}

// ── Load / Save ────────────────────────────────────────────────────────────

fn load(asset_path: []const u8) void {
    setBuf(loaded_path_buf[0..], asset_path);
    loaded_path_len = @min(asset_path.len, loaded_path_buf.len - 1);
    dirty = false;

    const def = UiTheme{};
    var t = def;
    if (std.Io.Dir.cwd().readFileAlloc(gui.io, asset_path, std.heap.page_allocator, .unlimited)) |bytes| {
        defer std.heap.page_allocator.free(bytes);
        if (UiTheme.loadFromBytes(std.heap.page_allocator, bytes)) |parsed| {
            t = parsed;
        } else |_| {}
    } else |_| {}
    defer if (t.name.ptr != def.name.ptr) std.heap.page_allocator.free(t.name);
    defer if (t.font_family.ptr != def.font_family.ptr) std.heap.page_allocator.free(t.font_family);

    setBuf(&name_buf, t.name);
    dark = t.dark;
    focus = t.focus;
    fill = t.fill;
    fill_hover = t.fill_hover;
    fill_press = t.fill_press;
    text = t.text;
    text_hover = t.text_hover;
    text_press = t.text_press;
    border = t.border;
    groups = .{ t.control, t.window, t.highlight, t.err, t.app1, t.app2, t.app3 };
    corner_kind = t.corner.kind;
    corner_rx = t.corner.rx;
    corner_y = t.corner.y;
    panel_border_width = t.panel_border_width;
    setBuf(&font_family_buf, t.font_family);
}

fn save() void {
    const t = UiTheme{
        .name = bufStr(&name_buf),
        .dark = dark,
        .focus = focus,
        .fill = fill,
        .fill_hover = fill_hover,
        .fill_press = fill_press,
        .text = text,
        .text_hover = text_hover,
        .text_press = text_press,
        .border = border,
        .control = groups[0],
        .window = groups[1],
        .highlight = groups[2],
        .err = groups[3],
        .app1 = groups[4],
        .app2 = groups[5],
        .app3 = groups[6],
        .corner = .{ .kind = corner_kind, .rx = corner_rx, .y = corner_y },
        .panel_border_width = panel_border_width,
        .font_family = bufStr(&font_family_buf),
    };
    t.save(gui.io, loadedPath()) catch return;
    dirty = false;
}

// ── Live preview ─────────────────────────────────────────────────────────────

/// Small sample panel (button/heading/body text) styled with this theme, next
/// to Studio's own chrome for comparison. Matches `PreviewSystem.LiveDrawFn`.
pub fn drawPreview(asset_path: []const u8, _: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    const current = UiTheme{
        .name = bufStr(&name_buf),
        .dark = dark,
        .focus = focus,
        .fill = fill,
        .fill_hover = fill_hover,
        .fill_press = fill_press,
        .text = text,
        .text_hover = text_hover,
        .text_press = text_press,
        .border = border,
        .control = groups[0],
        .window = groups[1],
        .highlight = groups[2],
        .err = groups[3],
        .app1 = groups[4],
        .app2 = groups[5],
        .app3 = groups[6],
        .corner = .{ .kind = corner_kind, .rx = corner_rx, .y = corner_y },
        .panel_border_width = panel_border_width,
        .font_family = bufStr(&font_family_buf),
    };
    const preview_theme = ui_render.theme.toDvuiTheme(current, gui.currentWindow().theme);

    var box = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .all(8), .theme = &preview_theme, .background = true });
    defer box.deinit();

    gui.label(@src(), "{s}", .{if (bufStr(&name_buf).len > 0) bufStr(&name_buf) else "Preview"}, .{ .font = .theme(.heading), .theme = &preview_theme, .id_extra = 1 });
    gui.label(@src(), "The quick brown fox jumps over the lazy dog", .{}, .{ .font = .theme(.body), .theme = &preview_theme, .id_extra = 2, .padding = .{ .y = 4 } });

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .theme = &preview_theme, .id_extra = 3 });
        defer row.deinit();
        _ = gui.button(@src(), "Control", .{}, .{ .theme = &preview_theme, .id_extra = 4 });
        _ = gui.button(@src(), "Highlight", .{}, .{ .theme = &preview_theme, .style = .highlight, .id_extra = 5 });
        _ = gui.button(@src(), "Error", .{}, .{ .theme = &preview_theme, .style = .err, .id_extra = 6 });
    }
}
