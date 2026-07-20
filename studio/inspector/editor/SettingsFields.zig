//! Draws `editor.StudioSettings`' reflected fields — one category at a time,
//! or flattened across a search — via the same `PropDraw` machinery every
//! other editor uses. Split out of `SettingsEditor.zig`, which owns the
//! `model`/`saved` state this operates on and passes it in by pointer.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const PropDraw = @import("../PropDraw.zig");
const StudioLocale = @import("../../services/StudioLocale.zig");
const tr = StudioLocale.tr;

var ui_theme_name_buf: [64]u8 = .{0} ** 64;
var ui_language_buf: [32]u8 = .{0} ** 32;
var system_font_path_buf: [1024]u8 = .{0} ** 1024;

/// Reseeds the theme/language/system-font buffers from `model` on next draw,
/// discarding stale contents from a previous session. Call after `load()`.
pub fn resetBuffers() void {
    setBuf(&ui_theme_name_buf, "");
    setBuf(&system_font_path_buf, "");
    setBuf(&ui_language_buf, "");
}

/// Draws each field of `selected_category`, individually, so each row gets
/// a dirty marker + revert button (`drawField`).
pub fn drawSelectedCategory(model: *editor.StudioSettings, saved: *const editor.StudioSettings, selected_category: usize) bool {
    var al = gui.Alignment.init(@src(), selected_category);
    defer al.deinit();
    var ctx = PropDraw.DrawCtx{ .al = &al, .allocator = std.heap.page_allocator };

    var changed = false;
    inline for (std.meta.fields(editor.StudioSettings), 0..) |field, fi| {
        if (fi == selected_category) {
            if (comptime std.mem.eql(u8, field.name, "ui")) {
                if (drawThemeRow(model)) changed = true;
                if (drawLanguageRow(model)) changed = true;
            }
            const CatT = field.type;
            inline for (std.meta.fields(CatT), 0..) |f, fj| {
                const hint = comptime fieldHintFor(CatT, f.name);
                if (comptime hint.hidden) continue;
                if (drawField(
                    f.type,
                    comptime PropDraw.displayLabel(f.name, hint),
                    &@field(@field(model, field.name), f.name),
                    &@field(@field(saved, field.name), f.name),
                    hint,
                    &ctx,
                    fj,
                )) changed = true;
            }
        }
    }
    return changed;
}

/// Theme dropdown + Import/Export, drawn ahead of the reflected `ui` category
/// fields — `theme_name` itself is `hidden` from the generic reflection loop
/// since its choices are runtime-dynamic (`editor.ThemeManager.list`).
fn drawThemeRow(model: *editor.StudioSettings) bool {
    ensureThemeNameBuf(model);
    if (bufStr(&system_font_path_buf).len == 0 and model.ui.system_font_path.len > 0)
        setBuf(&system_font_path_buf, model.ui.system_font_path);
    var changed = false;

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9000 });
        defer row.deinit();
        gui.label(@src(), "{s}", .{tr("Theme")}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 } });

        const themes_dir = themesDirOrEmpty();
        const entries = if (EditorState.settingsReady())
            editor.ThemeManager.list(gui.currentWindow().arena(), gui.io, themes_dir) catch &.{}
        else
            &.{};

        var dd: gui.DropdownWidget = undefined;
        dd.init(@src(), .{ .label = bufStr(&ui_theme_name_buf) }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 9000 });
        if (dd.dropped()) {
            for (entries) |entry| {
                if (dd.addChoiceLabel(entry.name)) {
                    setBuf(&ui_theme_name_buf, entry.name);
                    model.ui.theme_name = bufStr(&ui_theme_name_buf);
                    changed = true;
                    break;
                }
            }
        }
        dd.deinit();
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9001 });
        defer row.deinit();
        if (gui.button(@src(), tr("Import Theme..."), .{}, .{ .gravity_y = 0.5, .id_extra = 9001 })) importTheme();
        if (gui.button(@src(), tr("Export Theme..."), .{}, .{ .gravity_y = 0.5, .id_extra = 9002 })) exportTheme();
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9003 });
        defer row.deinit();
        gui.label(@src(), "{s}", .{tr("System Font")}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 } });
        const path = model.ui.system_font_path;
        gui.label(@src(), "{s}", .{if (path.len > 0) std.fs.path.basename(path) else tr("(theme default)")}, .{
            .gravity_y = 0.5,
            .expand = .horizontal,
            .id_extra = 9003,
        });
        if (gui.button(@src(), tr("Browse..."), .{}, .{ .gravity_y = 0.5, .id_extra = 9004 })) {
            if (gui.dialogNativeFileOpen(gui.currentWindow().arena(), .{ .filters = &.{ "*.ttf", "*.otf" } }) catch null) |picked| {
                setBuf(&system_font_path_buf, picked);
                model.ui.system_font_path = bufStr(&system_font_path_buf);
                changed = true;
            }
        }
        if (path.len > 0 and gui.button(@src(), tr("Clear"), .{}, .{ .gravity_y = 0.5, .id_extra = 9005 })) {
            setBuf(&system_font_path_buf, "");
            model.ui.system_font_path = bufStr(&system_font_path_buf);
            changed = true;
        }
    }
    return changed;
}

/// Language dropdown, drawn ahead of the reflected `ui` category fields —
/// `language` is `hidden` from the generic reflection loop since its choices
/// (`StudioLocale.available_languages`) aren't something `FieldHint` can
/// express, mirroring `drawThemeRow`.
fn drawLanguageRow(model: *editor.StudioSettings) bool {
    if (bufStr(&ui_language_buf).len == 0) setBuf(&ui_language_buf, model.ui.language);
    var changed = false;

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9006 });
    defer row.deinit();
    gui.label(@src(), "{s}", .{tr("Language")}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 } });

    const active = bufStr(&ui_language_buf);
    var display_label: []const u8 = active;
    for (StudioLocale.available_languages) |lang| {
        if (std.mem.eql(u8, lang.tag, active)) {
            display_label = lang.display_name;
            break;
        }
    }

    var dd: gui.DropdownWidget = undefined;
    dd.init(@src(), .{ .label = display_label }, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = 9006 });
    if (dd.dropped()) {
        for (StudioLocale.available_languages) |lang| {
            if (dd.addChoiceLabel(lang.display_name)) {
                setBuf(&ui_language_buf, lang.tag);
                model.ui.language = bufStr(&ui_language_buf);
                changed = true;
                break;
            }
        }
    }
    dd.deinit();
    return changed;
}

fn ensureThemeNameBuf(model: *const editor.StudioSettings) void {
    if (bufStr(&ui_theme_name_buf).len == 0) setBuf(&ui_theme_name_buf, model.ui.theme_name);
}

fn setBuf(dst: []u8, s: []const u8) void {
    const n = @min(s.len, dst.len - 1);
    @memcpy(dst[0..n], s[0..n]);
    @memset(dst[n..], 0);
}

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn themesDirOrEmpty() []const u8 {
    if (!EditorState.settingsReady()) return "";
    return editor.ThemeManager.themesDir(gui.currentWindow().arena(), EditorState.settings.global_path) catch "";
}

fn importTheme() void {
    if (!EditorState.settingsReady()) return;
    const src = gui.dialogNativeFileOpen(gui.currentWindow().arena(), .{ .filters = &.{"*.uitheme"} }) catch return orelse return;
    const themes_dir = themesDirOrEmpty();
    if (themes_dir.len == 0) return;
    editor.ThemeManager.importFile(gui.currentWindow().arena(), gui.io, themes_dir, src) catch return;
}

fn exportTheme() void {
    if (!EditorState.settingsReady()) return;
    const dest = gui.dialogNativeFileSave(gui.currentWindow().arena(), .{ .filters = &.{"*.uitheme"} }) catch return orelse return;
    const themes_dir = themesDirOrEmpty();
    if (themes_dir.len == 0) return;
    editor.ThemeManager.exportTo(gui.currentWindow().arena(), gui.io, themes_dir, bufStr(&ui_theme_name_buf), dest) catch return;
}

/// Flattens every category's fields and draws only those whose name,
/// tooltip, or category title match `search` (case-insensitive substring),
/// grouped under a category heading.
pub fn drawSearchResults(model: *editor.StudioSettings, saved: *const editor.StudioSettings, search: []const u8) bool {
    var al = gui.Alignment.init(@src(), 0);
    defer al.deinit();
    var ctx = PropDraw.DrawCtx{ .al = &al, .allocator = std.heap.page_allocator };

    var changed = false;
    var id: usize = 1;
    var any_match = false;

    inline for (std.meta.fields(editor.StudioSettings), 0..) |cat_field, ci| {
        const cat_meta = editor.studio_settings_categories[ci];
        const CatT = cat_field.type;
        var header_drawn = false;
        inline for (std.meta.fields(CatT)) |f| {
            const hint = comptime fieldHintFor(CatT, f.name);
            if (comptime hint.hidden) continue;
            if (fieldMatches(search, f.name, hint.tooltip, cat_meta.title)) {
                any_match = true;
                if (!header_drawn) {
                    gui.label(@src(), "{s}", .{cat_meta.title}, .{
                        .font = .theme(.heading),
                        .padding = .{ .y = 6 },
                        .id_extra = id,
                    });
                    header_drawn = true;
                }
                if (drawField(
                    f.type,
                    comptime PropDraw.displayLabel(f.name, hint),
                    &@field(@field(model, cat_field.name), f.name),
                    &@field(@field(saved, cat_field.name), f.name),
                    hint,
                    &ctx,
                    id,
                )) changed = true;
            }
            id += 1;
        }
    }

    if (!any_match) gui.label(@src(), "{s}", .{StudioLocale.trArgs("No settings match \"{query}\".", &.{.{ .name = "query", .value = .{ .text = search } }})}, .{ .padding = .all(4) });
    return changed;
}

fn fieldHintFor(comptime CatT: type, comptime name: []const u8) engine.FieldHint {
    if (@hasDecl(CatT, "turian_hints") and @hasDecl(CatT.turian_hints, name))
        return @field(CatT.turian_hints, name);
    return .{};
}

/// Draws one field's value row via `PropDraw.drawValue`, followed by a `*`
/// marker and a revert-to-saved button when it differs from `saved_ptr`.
/// Returns true if the live value changed this frame (including a revert).
fn drawField(
    comptime FieldT: type,
    label: []const u8,
    model_ptr: *FieldT,
    saved_ptr: *const FieldT,
    hint: engine.FieldHint,
    ctx: *PropDraw.DrawCtx,
    id: usize,
) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    var changed = PropDraw.drawValue(FieldT, label, model_ptr, hint, ctx, id);

    if (!std.meta.eql(model_ptr.*, saved_ptr.*)) {
        gui.label(@src(), "*", .{}, .{ .gravity_y = 0.5, .id_extra = id, .padding = .{ .x = 4 } });
        if (gui.buttonIcon(@src(), "revert", gui.entypo.back_in_time, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 20, .h = 20 },
            .id_extra = id,
        })) {
            model_ptr.* = saved_ptr.*;
            changed = true;
        }
    }

    return changed;
}

fn fieldMatches(search: []const u8, name: []const u8, tooltip: ?[]const u8, category: []const u8) bool {
    if (containsIgnoreCase(name, search)) return true;
    if (tooltip) |t| if (containsIgnoreCase(t, search)) return true;
    return containsIgnoreCase(category, search);
}

fn containsIgnoreCase(haystack: []const u8, needle: []const u8) bool {
    if (needle.len == 0) return true;
    if (needle.len > haystack.len) return false;
    var i: usize = 0;
    while (i + needle.len <= haystack.len) : (i += 1) {
        if (std.ascii.eqlIgnoreCase(haystack[i .. i + needle.len], needle)) return true;
    }
    return false;
}
