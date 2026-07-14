//! Built-in `.uitheme` presets shipped with the engine: Dark, Light, Dark
//! High Contrast, Darcula, and Catppuccin. Mirrors `Material.presets` — named,
//! comptime-constructed values rather than files on disk, so they're always
//! available even with no project open.
//!
//! `dark`/`light` deliberately soften the border contrast and narrow the gap
//! between panel-header and body fill colors versus dvui's stock Adwaita
//! theme (`zig-pkg/dvui-.../src/themes/Adwaita.zig`) — Adwaita's dark border
//! is `dark_fill_hsl.lighten(39)`, visually dominant against the 0x1e1e1e
//! body fill; ours is a fixed, much closer gray.
//!
//! Each preset also sets `app1` — repurposed by Studio as dock panel body
//! chrome (`studio/main-window/Window.zig`) — to a fill clearly distinct from
//! `window.fill` (used by the root canvas and tab strip), so a panel's
//! boundary is identifiable at a glance instead of blending into the rest of
//! the chrome. That fill contrast is what delineates a panel, so the presets
//! round the panel corners and draw no panel border at all; Dark High
//! Contrast is the exception, where a hard border *is* the accessibility
//! affordance.
const UiTheme = @import("UiTheme.zig");
const Style = UiTheme.Style;
const Color = UiTheme.Color;

fn rgb(r: u8, g: u8, b: u8) Color {
    return .{ .r = r, .g = g, .b = b };
}

pub const dark = UiTheme{
    .name = "Dark",
    .dark = true,
    .focus = rgb(0x35, 0x84, 0xe4),
    .fill = rgb(0x1e, 0x1e, 0x1e),
    .fill_hover = rgb(0x2a, 0x2a, 0x2a),
    .fill_press = rgb(0x33, 0x33, 0x33),
    .text = rgb(0xe6, 0xe6, 0xe6),
    .border = rgb(0x3a, 0x3a, 0x3a),
    .control = .{ .fill = rgb(0x2b, 0x2b, 0x2b), .fill_hover = rgb(0x33, 0x33, 0x33), .fill_press = rgb(0x3d, 0x3d, 0x3d) },
    .window = .{ .fill = rgb(0x23, 0x23, 0x23) },
    .highlight = .{ .fill = rgb(0x35, 0x84, 0xe4), .fill_hover = rgb(0x4c, 0x93, 0xe8), .fill_press = rgb(0x2e, 0x75, 0xcc), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0x5c, 0xa0, 0xee) },
    .err = .{ .fill = rgb(0xc0, 0x1c, 0x28), .fill_hover = rgb(0xd3, 0x3e, 0x49), .fill_press = rgb(0xa8, 0x12, 0x1d), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0xe2, 0x62, 0x6c) },
    .app1 = .{ .fill = rgb(0x18, 0x18, 0x18), .border = rgb(0x45, 0x45, 0x45) },
    .panel_border_width = 0,
    .panel_corner_radius = 6,
    .corner = .{ .kind = .round, .rx = 6 },
};

pub const light = UiTheme{
    .name = "Light",
    .dark = false,
    .focus = rgb(0x35, 0x84, 0xe4),
    .fill = rgb(0xff, 0xff, 0xff),
    .fill_hover = rgb(0xe8, 0xe8, 0xe8),
    .fill_press = rgb(0xd6, 0xd6, 0xd6),
    .text = rgb(0x1a, 0x1a, 0x1a),
    .border = rgb(0xc7, 0xc7, 0xc7),
    .control = .{ .fill = rgb(0xef, 0xef, 0xef), .fill_hover = rgb(0xe4, 0xe4, 0xe4), .fill_press = rgb(0xd2, 0xd2, 0xd2) },
    .window = .{ .fill = rgb(0xf5, 0xf5, 0xf5) },
    .highlight = .{ .fill = rgb(0x35, 0x84, 0xe4), .fill_hover = rgb(0x2a, 0x72, 0xc4), .fill_press = rgb(0x1f, 0x5f, 0xa8), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0x6b, 0xa3, 0xe8) },
    .err = .{ .fill = rgb(0xe0, 0x1b, 0x24), .fill_hover = rgb(0xc8, 0x17, 0x20), .fill_press = rgb(0xa8, 0x12, 0x1d), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0xee, 0x80, 0x87) },
    .app1 = .{ .fill = rgb(0xed, 0xed, 0xed), .border = rgb(0xb5, 0xb5, 0xb5) },
    .panel_border_width = 0,
    .panel_corner_radius = 6,
    .corner = .{ .kind = .round, .rx = 6 },
};

pub const dark_high_contrast = UiTheme{
    .name = "Dark High Contrast",
    .dark = true,
    .focus = rgb(0x4f, 0xc3, 0xf7),
    .fill = rgb(0x00, 0x00, 0x00),
    .fill_hover = rgb(0x1a, 0x1a, 0x1a),
    .fill_press = rgb(0x2a, 0x2a, 0x2a),
    .text = rgb(0xff, 0xff, 0xff),
    .border = rgb(0xff, 0xff, 0xff),
    .control = .{ .fill = rgb(0x10, 0x10, 0x10), .fill_hover = rgb(0x20, 0x20, 0x20), .fill_press = rgb(0x30, 0x30, 0x30) },
    .window = .{ .fill = rgb(0x14, 0x14, 0x14) },
    .highlight = .{ .fill = rgb(0x4f, 0xc3, 0xf7), .fill_hover = rgb(0x29, 0xb6, 0xf6), .fill_press = rgb(0x03, 0x9b, 0xe5), .text = rgb(0x00, 0x00, 0x00), .border = rgb(0xff, 0xff, 0xff) },
    .err = .{ .fill = rgb(0xff, 0x17, 0x44), .fill_hover = rgb(0xff, 0x52, 0x52), .fill_press = rgb(0xd5, 0x00, 0x00), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0xff, 0xff, 0xff) },
    .app1 = .{ .fill = rgb(0x00, 0x00, 0x00), .border = rgb(0xff, 0xff, 0xff) },
    .panel_border_width = 2,
    .panel_corner_radius = 0,
    .corner = .{ .kind = .square, .rx = 0 },
};

pub const darcula = UiTheme{
    .name = "Darcula",
    .dark = true,
    .focus = rgb(0x4a, 0x88, 0xc7),
    .fill = rgb(0x2b, 0x2b, 0x2b),
    .fill_hover = rgb(0x32, 0x32, 0x32),
    .fill_press = rgb(0x3c, 0x3f, 0x41),
    .text = rgb(0xa9, 0xb7, 0xc6),
    .border = rgb(0x54, 0x55, 0x56),
    .control = .{ .fill = rgb(0x3c, 0x3f, 0x41), .fill_hover = rgb(0x45, 0x47, 0x49), .fill_press = rgb(0x4e, 0x52, 0x54) },
    .window = .{ .fill = rgb(0x3c, 0x3f, 0x41) },
    .highlight = .{ .fill = rgb(0x4a, 0x88, 0xc7), .fill_hover = rgb(0x5a, 0x96, 0xd3), .fill_press = rgb(0x3a, 0x75, 0xb0), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0x68, 0x97, 0xc4) },
    .err = .{ .fill = rgb(0xbe, 0x3c, 0x3c), .fill_hover = rgb(0xcc, 0x55, 0x55), .fill_press = rgb(0xa3, 0x2b, 0x2b), .text = rgb(0xff, 0xff, 0xff), .border = rgb(0xd4, 0x6a, 0x6a) },
    .app1 = .{ .fill = rgb(0x2b, 0x2b, 0x2b), .border = rgb(0x6e, 0x6e, 0x6e) },
    .panel_border_width = 0,
    .panel_corner_radius = 4,
    .corner = .{ .kind = .round, .rx = 4 },
};

pub const catppuccin = UiTheme{
    .name = "Catppuccin",
    .dark = true,
    .focus = rgb(0xcb, 0xa6, 0xf7),
    .fill = rgb(0x1e, 0x1e, 0x2e),
    .fill_hover = rgb(0x31, 0x32, 0x44),
    .fill_press = rgb(0x45, 0x47, 0x5a),
    .text = rgb(0xcd, 0xd6, 0xf4),
    .border = rgb(0x45, 0x47, 0x5a),
    .control = .{ .fill = rgb(0x31, 0x32, 0x44), .fill_hover = rgb(0x3b, 0x3d, 0x52), .fill_press = rgb(0x45, 0x47, 0x5a) },
    .window = .{ .fill = rgb(0x18, 0x18, 0x25) },
    .highlight = .{ .fill = rgb(0xcb, 0xa6, 0xf7), .fill_hover = rgb(0xd8, 0xbf, 0xfa), .fill_press = rgb(0xb4, 0x8e, 0xe0), .text = rgb(0x1e, 0x1e, 0x2e), .border = rgb(0xcb, 0xa6, 0xf7) },
    .err = .{ .fill = rgb(0xf3, 0x8b, 0xa8), .fill_hover = rgb(0xf5, 0xa0, 0xb8), .fill_press = rgb(0xe0, 0x6e, 0x8e), .text = rgb(0x1e, 0x1e, 0x2e), .border = rgb(0xf3, 0x8b, 0xa8) },
    .app1 = .{ .fill = rgb(0x1e, 0x1e, 0x2e), .border = rgb(0x31, 0x32, 0x44) },
    .panel_border_width = 0,
    .panel_corner_radius = 6,
    .corner = .{ .kind = .round, .rx = 6 },
};

/// All built-in presets, in display order.
pub const all = [_]UiTheme{ dark, light, dark_high_contrast, darcula, catppuccin };
