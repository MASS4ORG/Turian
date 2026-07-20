//! Studio Settings editor: a category-organized, searchable editor for
//! Studio-wide configuration (`editor.StudioSettings`), split into a
//! "local" sidebar (`drawSidebar`, search + category list + Save) and a
//! "fields" panel (`drawFields`, drawn by the *global* `Inspector` panel
//! like any other selection) — the same global-Inspector/local-panel split
//! every other document tab uses (`studio/Window.zig`'s shared skeleton).
//!
//! Owns the `model`/`saved` state and the load/save/dirty lifecycle;
//! `SettingsFields.zig` draws the reflected fields against pointers into it,
//! and `ShortcutsEditor.zig` is this document tab's other editor (shares
//! its dirty indicator and search box — see `setDirty`/`currentSearch`).
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const Documents = @import("../../main-window/Documents.zig");
const AssetActions = @import("../../asset-browser/AssetActions.zig");
const EditorCamera = @import("../../scene-view/EditorCamera.zig");
const MenuBar = @import("../../main-window/MenuBar.zig");
const AssetTileLayout = @import("../../asset-browser/AssetTileLayout.zig");
const LayoutStore = @import("../../services/LayoutStore.zig");
const ActiveTheme = @import("../../services/ActiveTheme.zig");
const StudioLocale = @import("../../services/StudioLocale.zig");
const ShortcutsEditor = @import("ShortcutsEditor.zig");
const SettingsFields = @import("SettingsFields.zig");
const tr = StudioLocale.tr;

var model: editor.StudioSettings = .{};
/// Baseline for the per-field dirty marker (`*`) and revert button: the
/// last-loaded-or-saved state. `model` is the live, in-progress edit.
var saved: editor.StudioSettings = .{};
var loaded: bool = false;
var dirty: bool = false;
var selected_category: usize = 0;
/// Sentinel `selected_category` for the "Shortcuts" entry — one past the
/// reflected categories, since it isn't backed by a `StudioSettings` field.
const SHORTCUTS_CATEGORY = std.math.maxInt(usize);
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

/// True if a field edit is unsaved.
pub fn isDirty() bool {
    return dirty;
}

/// Search text from the shared sidebar search box, read by `ShortcutsEditor`
/// so it doesn't need a second one of its own.
pub fn currentSearch() []const u8 {
    return bufStr(&search_buf);
}

/// `ShortcutsEditor` shares this document tab and its dirty indicator —
/// clearing this editor's dirty flag must not clear the tab's if the other
/// editor still has unsaved rebinds.
fn setDirty(value: bool) void {
    dirty = value;
    Documents.setActiveDirty(dirty or ShortcutsEditor.isDirty());
}

/// (Re)populates the in-memory model from the on-disk-backed settings store,
/// discarding any unsaved edits.
pub fn load() void {
    model = editor.StudioSettings.fromSettings(&EditorState.settings);
    saved = model;
    loaded = true;
    setDirty(false);
    SettingsFields.resetBuffers();
}

/// Persists the in-memory model, pushes the live globals it mirrors — camera
/// speeds (`SceneViewport.zig`'s "Camera ▾" quick menu edits the same
/// `EditorCamera` vars directly), the editor-FPS overlay toggle
/// (`MenuBar.show_editor_fps`), and the asset browser's name-truncation
/// length + hide-extensions toggle (`AssetTileLayout`).
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
    StudioLocale.setLanguage(model.ui.language);
    saved = model;
    setDirty(false);
}

/// Applies the just-saved theme/font-size/zoom/system-font to the running
/// window — no restart needed.
fn applyUiTheme() void {
    const themes_dir = themesDirOrEmpty();
    if (themes_dir.len == 0) return;
    ActiveTheme.apply(gui.currentWindow(), gui.currentWindow().arena(), gui.io, themes_dir, model.ui.theme_name, model.ui.font_size, model.ui.zoom, model.ui.system_font_path);
}

fn themesDirOrEmpty() []const u8 {
    if (!EditorState.settingsReady()) return "";
    return editor.ThemeManager.themesDir(gui.currentWindow().arena(), EditorState.settings.global_path) catch "";
}

/// Local panel (left column, in place of a Hierarchy panel): search box,
/// Save, and the category list. Drawn by `Window.zig`'s shared panel
/// skeleton.
pub fn drawSidebar() void {
    ensureLoaded();

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();

        var te = gui.textEntry(@src(), .{ .text = .{ .buffer = search_buf[0..] } }, .{
            .expand = .horizontal,
            .padding = .{ .x = 4, .y = 4 },
        });
        te.deinit();

        const any_dirty = dirty or ShortcutsEditor.isDirty();
        if (gui.button(@src(), tr("Save"), .{}, .{ .gravity_y = 0.5, .style = if (any_dirty) .highlight else .control })) {
            // Both editors' state lives in one document tab; `saveActive`
            // (via `saveOne`'s `.studio_settings` branch) saves both.
            Documents.saveActive();
        }
    }

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
        if (gui.button(@src(), tr("Shortcuts"), .{}, .{
            .expand = .horizontal,
            .id_extra = SHORTCUTS_CATEGORY,
            .style = if (selected_category == SHORTCUTS_CATEGORY) .highlight else .control,
        })) {
            selected_category = SHORTCUTS_CATEGORY;
        }
    } else {
        // A search spans both settings fields and shortcuts together
        // (`drawFields`'s search branch), so there's no single category to
        // highlight here.
        gui.label(@src(), "{s}", .{tr("Search Results")}, .{ .expand = .horizontal, .font = .theme(.body), .padding = .all(4) });
    }
}

/// Fields panel: drawn by the *global* Inspector when the Settings tab is
/// active, exactly like a selected scene object's or asset's fields. A
/// search shows matching settings fields and matching shortcuts together;
/// otherwise the selected category's fields, or the Shortcuts list.
pub fn drawFields() void {
    ensureLoaded();
    const search = bufStr(&search_buf);

    if (search.len == 0) {
        if (selected_category == SHORTCUTS_CATEGORY) return ShortcutsEditor.drawFields();

        var changed = false;
        {
            var scroll = gui.scrollArea(@src(), .{}, .{
                .expand = .both,
                .style = .app1,
                .min_size_content = .{ .h = 0 },
                .max_size_content = .height(0),
            });
            defer scroll.deinit();
            changed = SettingsFields.drawSelectedCategory(&model, &saved, selected_category);
        }
        if (changed) setDirty(true);
        return;
    }

    var changed = false;
    {
        var scroll = gui.scrollArea(@src(), .{}, .{
            .expand = .both,
            .style = .app1,
            .min_size_content = .{ .h = 0 },
            .max_size_content = .height(0),
        });
        defer scroll.deinit();

        changed = SettingsFields.drawSearchResults(&model, &saved, search);

        _ = gui.separator(@src(), .{ .expand = .horizontal, .padding = .{ .y = 6 }, .id_extra = 500 });
        gui.label(@src(), "{s}", .{tr("Shortcuts")}, .{ .font = .theme(.heading), .padding = .{ .y = 6 }, .id_extra = 501 });
        ShortcutsEditor.drawRows(search);
    }
    if (changed) setDirty(true);
    ShortcutsEditor.drawCaptureDialog();
}

fn openJson() void {
    if (!EditorState.settingsReady()) return;
    const path = EditorState.settings.global_path;
    const dir = std.fs.path.dirname(path) orelse ".";
    const base = std.fs.path.basename(path);
    AssetActions.openExternal(dir, base);
}

/// Dock header "..." menu for the "settings" panel (`Panels.zig`'s
/// `PanelDesc.settings` slot) — maintenance actions for both this document
/// tab's editors, since they share one dock tab.
pub fn drawDockMenu(instance_id: []const u8) void {
    _ = instance_id;
    if (gui.menuItemLabel(@src(), tr("Reload Settings"), .{}, .{ .expand = .horizontal }) != null) {
        load();
    }
    if (gui.menuItemLabel(@src(), tr("Open Settings JSON"), .{}, .{ .expand = .horizontal, .id_extra = 1 }) != null) {
        openJson();
    }
    if (gui.menuItemLabel(@src(), tr("Reset Layout"), .{}, .{ .expand = .horizontal, .id_extra = 2 }) != null) {
        LayoutStore.reset(gui.io);
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4), .id_extra = 3 });

    if (gui.menuItemLabel(@src(), tr("Reload Shortcuts"), .{}, .{ .expand = .horizontal, .id_extra = 4 }) != null) {
        ShortcutsEditor.reload();
    }
    if (gui.menuItemLabel(@src(), tr("Reset All Shortcuts"), .{}, .{ .expand = .horizontal, .id_extra = 5 }) != null) {
        ShortcutsEditor.resetAll();
    }
}
