//! Studio Settings editor (issue #88): a category-organized, searchable
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
}

/// Persist the in-memory model, push the live globals it mirrors — camera
/// speeds (the same `EditorCamera.move_speed`/`look_sensitivity`/`zoom_speed`
/// `pub var`s `SceneViewport.zig`'s own "Camera ▾" quick menu edits directly)
/// and the editor-FPS overlay toggle (`MenuBar.show_editor_fps`) — and clear
/// the dirty flag. Called by the footer's Save button and by `Documents`'s
/// unsaved-changes close confirmation.
pub fn save() void {
    if (!EditorState.settingsReady()) return;
    model.applyToSettings(&EditorState.settings) catch return;
    EditorState.settings.save(gui.io);
    EditorCamera.move_speed = model.camera.move_speed;
    EditorCamera.look_sensitivity = model.camera.look_sensitivity;
    EditorCamera.zoom_speed = model.camera.zoom_speed;
    MenuBar.show_editor_fps = model.general.show_editor_fps;
    saved = model;
    dirty = false;
    Documents.setActiveDirty(false);
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
            const CatT = field.type;
            inline for (std.meta.fields(CatT), 0..) |f, fj| {
                const hint = comptime fieldHintFor(CatT, f.name);
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
}

fn openJson() void {
    if (!EditorState.settingsReady()) return;
    const path = EditorState.settings.global_path;
    const dir = std.fs.path.dirname(path) orelse ".";
    const base = std.fs.path.basename(path);
    AssetActions.openExternal(dir, base);
}
