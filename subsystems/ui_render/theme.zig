//! Converts an `engine.UiTheme` asset (pure data, no GUI dependency) into a
//! real `gui.Theme`. This is the single conversion point shared by Studio
//! (`editor/project/ThemeManager.zig`) and the shipped game's generated boot
//! code (`editor/build/codegen/MainZigCodegen.zig`) — the "close solution for
//! both" this module already provides for the runtime UI draw walk.
//!
//! `UiTheme` deliberately carries no font data (see its doc comment), so
//! conversion overlays its colors/corner onto `base`'s fonts/embedded_fonts/
//! ninepatches unchanged — no font (re)loading happens on a theme switch.
const gui = @import("gui");
const engine = @import("engine");
const UiTheme = engine.UiTheme;

fn color(c: UiTheme.Color) gui.Color {
    return .{ .r = c.r, .g = c.g, .b = c.b, .a = c.a };
}

fn maybeColor(c: ?UiTheme.Color) ?gui.Color {
    return if (c) |v| color(v) else null;
}

fn style(s: UiTheme.Style) gui.Theme.Style {
    return .{
        .fill = maybeColor(s.fill),
        .fill_hover = maybeColor(s.fill_hover),
        .fill_press = maybeColor(s.fill_press),
        .text = maybeColor(s.text),
        .text_hover = maybeColor(s.text_hover),
        .text_press = maybeColor(s.text_press),
        .border = maybeColor(s.border),
    };
}

fn corner(c: UiTheme.Corner) gui.Corner {
    return .{
        .kind = switch (c.kind) {
            .theme => .theme,
            .square => .square,
            .round => .round,
            .chamfer => .chamfer,
            .nudge => .nudge,
            .angular => .angular,
        },
        .rx = c.rx,
        .y = c.y,
    };
}

/// Returns `t` with all 4 fonts (body/heading/title/mono) resized so, once
/// rendered through `zoom` (dvui's `Window.content_scale`, which uniformly
/// scales every logical pixel including font rasterization — see
/// `Window.zig`'s `natural_scale` computation), the *physical* on-screen
/// `font_body` size lands exactly at `size` regardless of `zoom`. This is
/// what lets "Zoom" grow/shrink buttons, tabs, and menus without also
/// growing/shrinking legible text — pre-dividing here cancels the multiply
/// `content_scale` applies at render time (`render.zig`: `font.size * rs.s`).
pub fn withFontSize(t: gui.Theme, size: f32, zoom: f32) gui.Theme {
    var out = t;
    const z = if (zoom > 0.01) zoom else 1.0;
    return out.fontSizeAdd(size / z - out.font_body.size);
}

/// Overlay `t`'s colors/corner onto `base` (fonts/embedded_fonts/ninepatches
/// carried over untouched).
pub fn toDvuiTheme(t: UiTheme, base: gui.Theme) gui.Theme {
    var out = base;
    out.name = t.name;
    out.dark = t.dark;
    out.focus = color(t.focus);
    out.fill = color(t.fill);
    out.fill_hover = maybeColor(t.fill_hover);
    out.fill_press = maybeColor(t.fill_press);
    out.text = color(t.text);
    out.text_hover = maybeColor(t.text_hover);
    out.text_press = maybeColor(t.text_press);
    out.border = color(t.border);
    out.control = style(t.control);
    out.window = style(t.window);
    out.highlight = style(t.highlight);
    out.err = style(t.err);
    out.app1 = style(t.app1);
    out.app2 = style(t.app2);
    out.app3 = style(t.app3);
    out.corner = corner(t.corner);
    // `allocated_strings` must stay false: `out.name` aliases `t.name`, which
    // this function does not own, so `Theme.deinit` must never free it.
    out.allocated_strings = false;

    // Suggested family, best-effort: only takes effect if that family is
    // already loaded (base's embedded fonts, or a user-registered system
    // font) — otherwise dvui logs and falls back, no crash. Preserves each
    // font's existing size/weight/style/etc, set by `withFontSize` before
    // this call.
    if (t.font_family.len > 0) {
        const fam = gui.Font.array(t.font_family);
        out.font_body.family = fam;
        out.font_heading.family = fam;
        out.font_title.family = fam;
        out.font_mono.family = fam;
    }
    return out;
}

/// Overrides all 4 fonts' family to `family` (already-registered dvui family
/// name), preserving size/weight/style. Used to apply a user-picked system
/// font on top of whatever theme is active, independent of the theme's own
/// (optional) suggested `font_family`.
pub fn withFontFamily(t: gui.Theme, family: []const u8) gui.Theme {
    var out = t;
    if (family.len == 0) return out;
    const fam = gui.Font.array(family);
    out.font_body.family = fam;
    out.font_heading.family = fam;
    out.font_title.family = fam;
    out.font_mono.family = fam;
    return out;
}
