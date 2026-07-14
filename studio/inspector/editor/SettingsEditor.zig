//! Studio Settings editor : a category-organized, searchable
//! editor for Studio-wide configuration (`editor.StudioSettings`), split into
//! a "local" sidebar (`drawSidebar`, category list + search) and a "fields"
//! panel (`drawFields`, drawn by the *global* `Inspector` panel like any
//! other selection) — the same global-Inspector/global-Asset-Browser,
//! local-per-tab-panel split every other document tab uses
//! (`studio/Window.zig`'s shared panel skeleton).
//!
//! Deliberately reuses the same reflection machinery every other editor
//! (`Inspector.zig`, component fields) already uses — `PropDraw.drawValue`
//! for each field — rather than a parallel hand-rolled form.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const Documents = @import("../../main-window/Documents.zig");
const PropDraw = @import("../PropDraw.zig");
const AssetActions = @import("../../asset-browser/AssetActions.zig");
const EditorCamera = @import("../../scene-view/EditorCamera.zig");
const MenuBar = @import("../../main-window/MenuBar.zig");
const AssetTileLayout = @import("../../asset-browser/AssetTileLayout.zig");
const LayoutStore = @import("../../services/LayoutStore.zig");
const ActiveTheme = @import("../../services/ActiveTheme.zig");

var model: editor.StudioSettings = .{};
/// Baseline for the per-field dirty marker (`*`) and revert button: the
/// last-loaded-or-saved state. `model` is the live, in-progress edit.
var saved: editor.StudioSettings = .{};
var loaded: bool = false;
var dirty: bool = false;
var selected_category: usize = 0;
var search_buf: [128]u8 = .{0} ** 128;

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn ensureLoaded() void {
    if (loaded) return;
    if (!EditorState.settingsReady()) return;
    load();
}

/// (Re)populate the in-memory model from the on-disk-backed settings store,
/// discarding any unsaved edits. Used both for the initial load and the
/// editor's "Reload" action.
pub fn load() void {
    model = editor.StudioSettings.fromSettings(&EditorState.settings);
    saved = model;
    loaded = true;
    dirty = false;
    Documents.setActiveDirty(false);
    // Force `drawThemeRow` to reseed its buffer-backed fields (theme name,
    // system font path) from the freshly-loaded `model` instead of keeping
    // whatever a previous session/edit left in them.
    setBuf(&ui_theme_name_buf, "");
    setBuf(&system_font_path_buf, "");
}

/// Persist the in-memory model, push the live globals it mirrors — camera
/// speeds (the same `EditorCamera.move_speed`/`look_sensitivity`/`zoom_speed`
/// `pub var`s `SceneViewport.zig`'s own "Camera ▾" quick menu edits directly)
/// the editor-FPS overlay toggle (`MenuBar.show_editor_fps`), and the asset
/// browser's name-truncation length + hide-extensions toggle
/// (`AssetTileLayout.max_name_chars`/`hide_extensions`) — and clear the dirty
/// flag. Called by the footer's Save button and by `Documents`'s
/// unsaved-changes close confirmation.
pub fn save() void {
    if (!EditorState.settingsReady()) return;
    model.applyToSettings(&EditorState.settings) catch return;
    EditorState.settings.save(gui.io);
    EditorCamera.move_speed = model.camera.move_speed;
    EditorCamera.look_sensitivity = model.camera.look_sensitivity;
    EditorCamera.zoom_speed = model.camera.zoom_speed;
    MenuBar.show_editor_fps = model.general.show_editor_fps;
    AssetTileLayout.max_name_chars = model.asset_browser.name_char_length;
    AssetTileLayout.hide_extensions = model.asset_browser.hide_extensions;
    applyUiTheme();
    saved = model;
    dirty = false;
    Documents.setActiveDirty(false);
}

/// Apply the just-saved theme/font-size/zoom/system-font to the running
/// window — no restart needed. Thin wrapper over `ActiveTheme.apply`, the
/// same one `Main.zig`'s boot-time `applyPersistedUiSettings` and
/// `ThemeMenu`'s hover-preview/commit use.
fn applyUiTheme() void {
    const themes_dir = themesDirOrEmpty();
    if (themes_dir.len == 0) return;
    ActiveTheme.apply(gui.currentWindow(), gui.currentWindow().arena(), gui.io, themes_dir, model.ui.theme_name, model.ui.font_size, model.ui.zoom, model.ui.system_font_path);
}

/// Local panel (left column, in place of a Hierarchy panel): search box +
/// category list. Drawn by `Window.zig`'s shared panel skeleton.
pub fn drawSidebar() void {
    ensureLoaded();

    var te = gui.textEntry(@src(), .{ .text = .{ .buffer = search_buf[0..] } }, .{
        .expand = .horizontal,
        .padding = .{ .x = 4, .y = 4 },
    });
    te.deinit();

    _ = gui.separator(@src(), .{ .expand = .horizontal });

    if (bufStr(&search_buf).len == 0) {
        for (editor.studio_settings_categories, 0..) |cat, ci| {
            const is_sel = ci == selected_category;
            if (gui.button(@src(), cat.title, .{}, .{
                .expand = .horizontal,
                .id_extra = ci,
                .style = if (is_sel) .highlight else .control,
            })) {
                selected_category = ci;
            }
        }
    } else {
        gui.label(@src(), "Search Results", .{}, .{ .expand = .horizontal, .font = .theme(.body), .padding = .all(4) });
    }
}

/// Fields panel: drawn by the *global* Inspector when the Settings tab is
/// active (`Inspector.zig`'s per-tab dispatch), exactly like a selected scene
/// object's or asset's fields. Selected-category fields, or flattened search
/// results, plus the Save/Reload/Open JSON footer.
pub fn drawFields() void {
    ensureLoaded();

    var changed = false;
    {
        var scroll = gui.scrollArea(@src(), .{}, .{
            .expand = .both,
            .style = .app1,
            .min_size_content = .{ .h = 0 },
            .max_size_content = .height(0),
        });
        defer scroll.deinit();

        const search = bufStr(&search_buf);
        if (search.len == 0) {
            changed = drawSelectedCategory() or changed;
        } else {
            changed = drawSearchResults(search) or changed;
        }
    }

    if (changed) {
        dirty = true;
        Documents.setActiveDirty(true);
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 1 });
    drawFooter();
}

/// Draw each field of the currently-selected category, individually, so each
/// row gets a dirty marker + revert button (`drawField`).
fn drawSelectedCategory() bool {
    var al = gui.Alignment.init(@src(), selected_category);
    defer al.deinit();
    var ctx = PropDraw.DrawCtx{ .al = &al, .allocator = std.heap.page_allocator };

    var changed = false;
    inline for (std.meta.fields(editor.StudioSettings), 0..) |field, fi| {
        if (fi == selected_category) {
            if (comptime std.mem.eql(u8, field.name, "ui")) {
                if (drawThemeRow()) changed = true;
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

var ui_theme_name_buf: [64]u8 = .{0} ** 64;

/// Theme dropdown + Import/Export, drawn ahead of the reflected `ui` category
/// fields (font size / zoom) — `theme_name` itself is `hidden` from the
/// generic reflection loop since its choices are runtime-dynamic
/// (`editor.ThemeManager.list`), not something `FieldHint` can express.
fn drawThemeRow() bool {
    ensureThemeNameBuf();
    if (bufStr(&system_font_path_buf).len == 0 and model.ui.system_font_path.len > 0)
        setBuf(&system_font_path_buf, model.ui.system_font_path);
    var changed = false;

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9000 });
        defer row.deinit();
        gui.label(@src(), "Theme", .{}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 } });

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
        if (gui.button(@src(), "Import Theme...", .{}, .{ .gravity_y = 0.5, .id_extra = 9001 })) importTheme();
        if (gui.button(@src(), "Export Theme...", .{}, .{ .gravity_y = 0.5, .id_extra = 9002 })) exportTheme();
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 9003 });
        defer row.deinit();
        gui.label(@src(), "System Font", .{}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 } });
        const path = model.ui.system_font_path;
        gui.label(@src(), "{s}", .{if (path.len > 0) std.fs.path.basename(path) else "(theme default)"}, .{
            .gravity_y = 0.5,
            .expand = .horizontal,
            .id_extra = 9003,
        });
        if (gui.button(@src(), "Browse...", .{}, .{ .gravity_y = 0.5, .id_extra = 9004 })) {
            if (gui.dialogNativeFileOpen(gui.currentWindow().arena(), .{ .filters = &.{ "*.ttf", "*.otf" } }) catch null) |picked| {
                setBuf(&system_font_path_buf, picked);
                model.ui.system_font_path = bufStr(&system_font_path_buf);
                changed = true;
            }
        }
        if (path.len > 0 and gui.button(@src(), "Clear", .{}, .{ .gravity_y = 0.5, .id_extra = 9005 })) {
            setBuf(&system_font_path_buf, "");
            model.ui.system_font_path = bufStr(&system_font_path_buf);
            changed = true;
        }
    }
    return changed;
}

var system_font_path_buf: [1024]u8 = .{0} ** 1024;

fn ensureThemeNameBuf() void {
    if (bufStr(&ui_theme_name_buf).len == 0) setBuf(&ui_theme_name_buf, model.ui.theme_name);
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

/// Flatten every category's fields and draw only those whose name, tooltip
/// ("description"), or category title match `search` (case-insensitive
/// substring), grouped under a category heading.
fn drawSearchResults(search: []const u8) bool {
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

    if (!any_match) gui.label(@src(), "No settings match \"{s}\".", .{search}, .{ .padding = .all(4) });
    return changed;
}

fn fieldHintFor(comptime CatT: type, comptime name: []const u8) engine.FieldHint {
    if (@hasDecl(CatT, "turian_hints") and @hasDecl(CatT.turian_hints, name))
        return @field(CatT.turian_hints, name);
    return .{};
}

/// Draw one field's value row via `PropDraw.drawValue`, followed by a `*`
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

fn drawFooter() void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
    defer row.deinit();

    if (dirty)
        gui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal })
    else
        gui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });

    if (gui.button(@src(), "Open JSON", .{}, .{ .gravity_y = 0.5 })) {
        openJson();
    }
    if (gui.button(@src(), "Reload", .{}, .{ .gravity_y = 0.5, .id_extra = 1 })) {
        load();
    }
    if (gui.button(@src(), "Save", .{}, .{ .gravity_y = 0.5, .id_extra = 2, .style = if (dirty) .highlight else .control })) {
        save();
    }
    if (gui.button(@src(), "Reset Layout", .{}, .{ .gravity_y = 0.5, .id_extra = 3 })) {
        LayoutStore.reset(gui.io);
    }
}

fn openJson() void {
    if (!EditorState.settingsReady()) return;
    const path = EditorState.settings.global_path;
    const dir = std.fs.path.dirname(path) orelse ".";
    const base = std.fs.path.basename(path);
    AssetActions.openExternal(dir, base);
}
