//! Central "apply a theme to the running window" helper — the one place
//! Studio boot (`Main.zig`), the Settings ▸ UI panel (`SettingsEditor.zig`),
//! and the View ▸ Theme menu's hover-preview/commit (`ThemeMenu.zig`) all
//! call into, so the resolve → convert → `themeSet` dance (plus applying
//! `zoom`, decoupled font size, and an optional system font override) lives
//! in exactly one place.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const ui_render = @import("ui_render");

/// Dock panel chrome of the currently-applied theme, in logical pixels.
/// Neither is a `gui.Theme` field (dvui has no notion of a dock panel), so
/// `Window.zig` reads them from here instead of trying to recover them from
/// the converted `gui.Theme`.
pub var panel_border_width: f32 = 0;
pub var panel_corner_radius: f32 = 0;

/// Resolve `theme_name` (built-in or user, via `ThemeManager`) and apply it
/// to `win`: colors/corner, `panel_border_width`, `zoom`
/// (`Window.content_scale`), a decoupled absolute `font_size`, and an
/// optional `system_font_path` override on top of everything else. Leaves
/// the window's current theme untouched if `theme_name` isn't found (e.g. a
/// deleted user theme) — callers don't need their own fallback.
///
/// Takes `win` explicitly rather than calling `gui.currentWindow()`: Studio's
/// boot-time call (`Main.zig`) runs before the first `Window.begin`, when
/// `dvui.current_window` is still null and `currentWindow()` would panic.
pub fn apply(
    win: *gui.Window,
    gpa: std.mem.Allocator,
    io: std.Io,
    themes_dir: []const u8,
    theme_name: []const u8,
    font_size: f32,
    zoom: f32,
    system_font_path: []const u8,
) void {
    win.content_scale = zoom;

    // Start from a fixed, pristine font base — never `win.theme` itself.
    // `win.theme` accumulates whatever family a *previous* `apply()` call
    // left behind (a prior system-font override, an old theme's suggested
    // `font_family`), so building on it would make "revert to default" a
    // no-op: nothing below ever resets `.family` back to the embedded
    // Vera Sans/Vera Sans Mono unless this exact call sets it again.
    // `adwaita_light`/`adwaita_dark` share the identical `embedded_fonts`
    // list, so which variant is irrelevant here — only its fonts matter.
    var base = ui_render.theme.withFontSize(gui.Theme.builtin.adwaita_dark, font_size, zoom);
    if (editor.ThemeManager.resolve(gpa, io, themes_dir, theme_name)) |*resolved| {
        var r = resolved.*;
        defer r.deinit(gpa);
        base = ui_render.theme.toDvuiTheme(r.theme, base);
        panel_border_width = r.theme.panel_border_width;
        panel_corner_radius = r.theme.panel_corner_radius;
    }

    if (system_font_path.len > 0) {
        const SystemFont = @import("SystemFont.zig");
        if (SystemFont.ensure(win, io, system_font_path)) |family| {
            base = ui_render.theme.withFontFamily(base, family);
        }
    }

    win.themeSet(base);
}
