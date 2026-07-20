//! Menu-item variants that mark an entry as opening a submenu by appending a
//! chevron icon. A literal "▸"/"▾" character is not an option: Vera Sans (the
//! default embedded font) has no glyph for either and renders a tofu box, so
//! the indicator is drawn as an entypo icon instead.
const std = @import("std");
const gui = @import("gui");
const Shortcuts = @import("services/Shortcuts.zig");

/// A regular (non-submenu) menu entry that shows `command_id`'s live
/// shortcut label right-aligned and dimmed next to the title. Returns true
/// on click, exactly like `gui.menuItemLabel`.
pub fn command(src: std.builtin.SourceLocation, title: []const u8, command_id: []const u8, opts: gui.Options) bool {
    var mi = gui.menuItem(src, .{}, opts);
    defer mi.deinit();

    const clicked = mi.activeRect() != null;
    const id_extra = opts.id_extra orelse 0;

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id_extra });
    defer row.deinit();

    gui.labelNoFmt(@src(), title, .{}, mi.style().strip().override(.{ .gravity_y = 0.5 }));

    const shortcut_label = Shortcuts.label(command_id);
    if (shortcut_label.len > 0) {
        _ = gui.spacer(@src(), .{ .expand = .horizontal, .min_size_content = .{ .w = 16 } });
        const dim = gui.themeGet().color(.window, .text).opacity(0.55);
        gui.labelNoFmt(@src(), shortcut_label, .{}, mi.style().strip().override(.{
            .gravity_y = 0.5,
            .color_text = dim,
            .id_extra = id_extra,
        }));
    }

    return clicked;
}

/// Submenu entry inside a *vertical* menu (a main-menu drop-down): label on
/// the left, right-pointing chevron pushed to the trailing edge.
pub fn submenu(src: std.builtin.SourceLocation, label_str: []const u8, opts: gui.Options) ?gui.Rect.Natural {
    return draw(src, label_str, gui.entypo.chevron_small_right, true, opts);
}

/// Submenu entry inside a *horizontal* menu (a toolbar drop-down): label with
/// a down-pointing chevron directly after it.
pub fn dropdown(src: std.builtin.SourceLocation, label_str: []const u8, opts: gui.Options) ?gui.Rect.Natural {
    return draw(src, label_str, gui.entypo.chevron_small_down, false, opts);
}

fn draw(
    src: std.builtin.SourceLocation,
    label_str: []const u8,
    chevron: []const u8,
    trailing: bool,
    opts: gui.Options,
) ?gui.Rect.Natural {
    var mi = gui.menuItem(src, .{ .submenu = true }, opts);
    defer mi.deinit();

    const ret = mi.activeRect();
    const id_extra = opts.id_extra orelse 0;

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id_extra });
    defer row.deinit();

    gui.labelNoFmt(@src(), label_str, .{}, mi.style().strip().override(.{ .gravity_y = 0.5 }));

    // Vertical menus right-align the chevron against the widest entry;
    // horizontal ones keep it snug against the label.
    if (trailing) _ = gui.spacer(@src(), .{ .expand = .horizontal, .min_size_content = .{ .w = 12 } });

    gui.icon(@src(), "submenu_chevron", chevron, .{}, mi.style().strip().override(.{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 10, .h = 10 },
        .id_extra = id_extra,
    }));

    return ret;
}
