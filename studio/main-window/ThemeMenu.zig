//! View ▸ Theme: a cascaded theme picker with mouse-hover live preview
//! (applies instantly, commits nothing) and click-to-select (persists +
//! closes), plus instant Font Size / Zoom controls — no separate Save step,
//! unlike Settings ▸ UI. All three share `ActiveTheme.apply`.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const ActiveTheme = @import("../services/ActiveTheme.zig");
const MenuItems = @import("../MenuItems.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const StudioSettings = editor.StudioSettings;

/// True only during frames the Theme submenu itself is open. Distinguishes
/// "closed without picking" (revert the preview) from "still browsing".
var theme_submenu_open: bool = false;
var previewed_buf: [64]u8 = .{0} ** 64;

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn setBuf(dst: []u8, s: []const u8) void {
    const n = @min(s.len, dst.len - 1);
    @memcpy(dst[0..n], s[0..n]);
    @memset(dst[n..], 0);
}

fn themesDirOrEmpty() []const u8 {
    if (!EditorState.settingsReady()) return "";
    return editor.ThemeManager.themesDir(gui.currentWindow().arena(), EditorState.settings.global_path) catch "";
}

const Current = struct { font_size: f32, zoom: f32, system_font_path: []const u8 };

fn current() Current {
    const s = &EditorState.settings;
    return .{
        .font_size = @floatCast(s.getFloat(StudioSettings.KEY_UI_FONT_SIZE, 9.0)),
        .zoom = @floatCast(s.getFloat(StudioSettings.KEY_UI_ZOOM, 1.0)),
        .system_font_path = s.getString(StudioSettings.KEY_UI_SYSTEM_FONT_PATH, ""),
    };
}

fn persistedThemeName() []const u8 {
    if (!EditorState.settingsReady()) return "Dark";
    return EditorState.settings.getString(StudioSettings.KEY_UI_THEME_NAME, "Dark");
}

fn applyByName(name: []const u8) void {
    if (!EditorState.settingsReady()) return;
    const themes_dir = themesDirOrEmpty();
    if (themes_dir.len == 0) return;
    const cur = current();
    ActiveTheme.apply(gui.currentWindow(), gui.currentWindow().arena(), gui.io, themes_dir, name, cur.font_size, cur.zoom, cur.system_font_path);
}

fn commit(name: []const u8) void {
    if (!EditorState.settingsReady()) return;
    EditorState.settings.setString(StudioSettings.KEY_UI_THEME_NAME, name) catch return;
    EditorState.settings.save(gui.io);
    applyByName(name);
}

/// Called every frame the parent **View** menu is open (regardless of
/// whether the Theme submenu specifically is expanded) — draws the "Theme"
/// cascaded entry plus the Font Size / Zoom rows. `m` is the View menu,
/// closed on a theme click like every other View item.
pub fn draw(m: *gui.MenuWidget) void {
    theme_submenu_open = false;

    if (MenuItems.submenu(@src(), tr("Theme"), .{ .expand = .horizontal })) |r| {
        theme_submenu_open = true;
        var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (!EditorState.settingsReady()) {
            gui.label(@src(), "{s}", .{tr("Settings not ready")}, .{ .padding = .all(8) });
        } else {
            const themes_dir = themesDirOrEmpty();
            const entries = editor.ThemeManager.list(gui.currentWindow().arena(), gui.io, themes_dir) catch &.{};
            var any_hover = false;

            for (entries, 0..) |entry, i| {
                var row = gui.box(@src(), .{}, .{ .expand = .horizontal, .id_extra = i });
                const clicked = gui.menuItemLabel(@src(), entry.name, .{}, .{ .expand = .horizontal, .id_extra = i }) != null;
                const hovered = row.data().rectScale().r.contains(gui.currentWindow().mouse_pt);
                row.deinit();

                if (hovered) {
                    any_hover = true;
                    if (!std.mem.eql(u8, bufStr(&previewed_buf), entry.name)) {
                        setBuf(&previewed_buf, entry.name);
                        applyByName(entry.name);
                    }
                }
                if (clicked) {
                    setBuf(&previewed_buf, "");
                    commit(entry.name);
                    m.close();
                }
            }

            // Mouse moved to a non-entry part of the still-open submenu:
            // revert the preview until it lands on an entry again.
            if (!any_hover and bufStr(&previewed_buf).len > 0) {
                setBuf(&previewed_buf, "");
                applyByName(persistedThemeName());
            }
        }
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 9100 });
    drawFontSizeZoom();
}

/// Call unconditionally, once per frame, from whatever draws the View menu —
/// on any frame `draw` above did NOT run (the View menu itself closed), so a
/// hover-preview left active when the user dismissed the menu without
/// clicking (Escape, click elsewhere) still gets reverted. `draw` cannot
/// detect this itself: it simply isn't called on the frame its parent closes.
pub fn revertIfViewClosed() void {
    if (theme_submenu_open) return;
    if (bufStr(&previewed_buf).len == 0) return;
    setBuf(&previewed_buf, "");
    applyByName(persistedThemeName());
}

/// Debounce interval for Font Size / Zoom text fields — avoids relayout
/// feedback-loop from a live-dragging slider.
const DEBOUNCE_NS: i128 = 500_000_000;

/// 0 = no pending edit (display tracks the persisted value every frame).
/// A debounced keystroke applies the last valid typed number.
var font_size_value: f32 = 9.0;
var font_size_last_change_ns: i128 = 0;
var zoom_value: f32 = 1.0;
var zoom_last_change_ns: i128 = 0;

fn drawFontSizeZoom() void {
    if (!EditorState.settingsReady()) return;
    const cur = current();
    if (font_size_last_change_ns == 0) font_size_value = cur.font_size;
    if (zoom_last_change_ns == 0) zoom_value = cur.zoom;

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = 9101 });
        defer row.deinit();
        gui.label(@src(), "{s}", .{tr("Font Size")}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 90 } });
        const r = gui.textEntryNumber(@src(), f32, .{ .value = &font_size_value, .min = 8, .max = 20 }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 9101 });
        if (r.changed) font_size_last_change_ns = gui.frameTimeNS();
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = 9102 });
        defer row.deinit();
        gui.label(@src(), "{s}", .{tr("Zoom")}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 90 } });
        const r = gui.textEntryNumber(@src(), f32, .{ .value = &zoom_value, .min = 0.7, .max = 1.5 }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 9102 });
        if (r.changed) zoom_last_change_ns = gui.frameTimeNS();
    }

    const now = gui.frameTimeNS();
    if (font_size_last_change_ns != 0 and now - font_size_last_change_ns >= DEBOUNCE_NS) {
        EditorState.settings.setFloat(StudioSettings.KEY_UI_FONT_SIZE, font_size_value) catch {};
        EditorState.settings.save(gui.io);
        applyByName(persistedThemeName());
        font_size_last_change_ns = 0;
    }
    if (zoom_last_change_ns != 0 and now - zoom_last_change_ns >= DEBOUNCE_NS) {
        EditorState.settings.setFloat(StudioSettings.KEY_UI_ZOOM, zoom_value) catch {};
        EditorState.settings.save(gui.io);
        applyByName(persistedThemeName());
        zoom_last_change_ns = 0;
    }
}
