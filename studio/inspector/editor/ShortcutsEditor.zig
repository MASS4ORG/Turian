//! Keybindings editor — the "Shortcuts" pseudo-category of the Settings
//! document (dispatched by `SettingsEditor.zig`, since commands are a
//! dynamic runtime list, not a fixed struct). Reuses `SettingsEditor`'s
//! search box (`currentSearch`); Reload/Reset All live in its "..." dock
//! menu (`drawDockMenu`) alongside the same actions for settings fields.
//!
//! Rebinds apply immediately to the live registry; Save/Reload only control
//! whether that state is persisted to, or discarded back to, the settings
//! file.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const Documents = @import("../../main-window/Documents.zig");
const Shortcuts = @import("../../services/Shortcuts.zig");
const SettingsEditor = @import("SettingsEditor.zig");
const StudioLocale = @import("../../services/StudioLocale.zig");
const tr = StudioLocale.tr;

var loaded: bool = false;
var dirty: bool = false;

/// One command's binding slot being captured, if any: `index = null` appends
/// a new binding, `index = n` replaces the nth one.
const CaptureTarget = struct { id: []const u8, index: ?usize };
var capturing: ?CaptureTarget = null;

fn ensureLoaded() void {
    if (loaded) return;
    if (!EditorState.settingsReady()) return;
    Shortcuts.ensureOverridesLoaded(&EditorState.settings);
    loaded = true;
}

/// True if a rebind is unsaved.
pub fn isDirty() bool {
    return dirty;
}

fn setDirty(value: bool) void {
    dirty = value;
    Documents.setActiveDirty(dirty or hasOtherUnsavedChanges());
}

/// `SettingsEditor` and this editor share one Settings document tab and thus
/// one dirty indicator — don't let this editor's `save()`/`reload()` stomp a
/// dirty flag the *other* editor's unsaved edits still need.
fn hasOtherUnsavedChanges() bool {
    return SettingsEditor.isDirty();
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

fn contextLabel(context: editor.shortcuts.Context) []const u8 {
    return switch (context) {
        .global => tr("Global"),
        .scene_viewport => tr("Scene View"),
        .hierarchy => tr("Hierarchy"),
        .asset_browser => tr("Asset Browser"),
        .ui_editor => tr("UI Editor"),
        .text_input => tr("Text Input"),
    };
}

/// `entry.desc.title` is a runtime value, not a comptime literal `tr()` can
/// accept — translated by explicit id lookup instead. Keep in sync with
/// every `CommandDesc.title`; a missing id just shows untranslated English.
fn translatedTitle(entry: editor.shortcuts.Entry) []const u8 {
    const id = entry.desc.id;
    const table = [_]struct { id: []const u8, title: []const u8 }{
        .{ .id = "edit.undo", .title = tr("Undo") },
        .{ .id = "edit.redo", .title = tr("Redo") },
        .{ .id = "edit.copy", .title = tr("Copy") },
        .{ .id = "edit.cut", .title = tr("Cut") },
        .{ .id = "edit.paste", .title = tr("Paste") },
        .{ .id = "play.toggle", .title = tr("Play / Stop") },
        .{ .id = "file.save", .title = tr("Save") },
        .{ .id = "file.saveAll", .title = tr("Save All") },
        .{ .id = "document.close", .title = tr("Close Tab") },
        .{ .id = "document.nextTab", .title = tr("Next Tab") },
        .{ .id = "document.prevTab", .title = tr("Previous Tab") },
        .{ .id = "sceneView.translateMode", .title = tr("Move Tool") },
        .{ .id = "sceneView.rotateMode", .title = tr("Rotate Tool") },
        .{ .id = "sceneView.scaleMode", .title = tr("Scale Tool") },
        .{ .id = "sceneView.focusSelection", .title = tr("Focus Selection") },
    };
    for (table) |row| {
        if (std.mem.eql(u8, row.id, id)) return row.title;
    }
    return entry.desc.title;
}

/// Drawn by `SettingsEditor.zig` when the Shortcuts category is selected and
/// the search box is empty. A non-empty search instead goes through
/// `SettingsEditor.drawFields`'s combined-results branch, which calls
/// `drawRows`/`drawCaptureDialog` directly.
pub fn drawFields() void {
    {
        var scroll = gui.scrollArea(@src(), .{}, .{
            .expand = .both,
            .style = .app1,
            .min_size_content = .{ .h = 0 },
            .max_size_content = .height(0),
        });
        defer scroll.deinit();
        drawRows("");
    }
    drawCaptureDialog();
}

/// The command list, optionally filtered by `search`. Self-contained (safe
/// to call from either `drawFields` above or `SettingsEditor`'s combined
/// search results) — loads overrides on first use regardless of caller.
pub fn drawRows(search: []const u8) void {
    ensureLoaded();
    const reg = Shortcuts.registry();
    const conflicts = reg.conflicts(gui.currentWindow().arena()) catch &.{};

    var al = gui.Alignment.init(@src(), 0);
    defer al.deinit();

    var id: usize = 0;
    var any_match = false;
    for (reg.commands()) |entry| {
        const title = translatedTitle(entry);
        const bindings = reg.effectiveBindings(entry.desc.id);
        const search_text = bindingsSearchText(bindings);

        // Matches title or binding text, not the id — its dotted namespace
        // ("document.close") produces false hits on short queries like "do".
        if (search.len > 0 and !containsIgnoreCase(title, search) and !containsIgnoreCase(search_text, search))
            continue;
        any_match = true;
        drawRow(reg, entry, title, bindings, conflicts, id);
        id += 1;
    }

    if (!any_match) gui.label(@src(), "{s}", .{StudioLocale.trArgs("No shortcuts match \"{query}\".", &.{.{ .name = "query", .value = .{ .text = search } }})}, .{ .padding = .all(4) });
}

fn drawRow(
    reg: *editor.shortcuts.Registry,
    entry: editor.shortcuts.Entry,
    title: []const u8,
    bindings: []const editor.shortcuts.Binding,
    conflicts: []const editor.shortcuts.Conflict,
    id: usize,
) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    gui.label(@src(), "{s}", .{title}, .{ .gravity_y = 0.5, .expand = .horizontal, .id_extra = id });
    gui.label(@src(), "{s}", .{contextLabel(entry.desc.context)}, .{
        .gravity_y = 0.5,
        .id_extra = id,
        .font = .theme(.body),
        .padding = .{ .x = 6 },
    });

    if (hasConflict(entry.desc.id, conflicts)) |other| {
        var wd: gui.WidgetData = undefined;
        gui.icon(@src(), "conflict", gui.entypo.warning, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 14, .h = 14 },
            .id_extra = id,
            .color_text = .{ .r = 0xe0, .g = 0xb0, .b = 0x30 },
            .data_out = &wd,
        });
        gui.tooltip(@src(), .{ .active_rect = wd.rectScale().r }, "{s}", .{
            StudioLocale.trArgs("Also bound to \"{other}\"", &.{.{ .name = "other", .value = .{ .text = other } }}),
        }, .{ .id_extra = id });
    }

    if (bindings.len == 0) gui.label(@src(), "{s}", .{tr("Unbound")}, .{ .gravity_y = 0.5, .id_extra = id, .padding = .{ .x = 4 } });
    for (bindings, 0..) |b, bi| drawBindingChip(entry.desc.id, b, bi);

    if (bindings.len < editor.shortcuts.MAX_OVERRIDE_BINDINGS) {
        if (gui.buttonIcon(@src(), tr("Add a binding"), gui.entypo.plus, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 18, .h = 18 },
            .id_extra = 50,
        })) {
            capturing = .{ .id = entry.desc.id, .index = null };
        }
    }

    if (reg.isOverridden(entry.desc.id)) {
        if (gui.buttonIcon(@src(), tr("Reset to default"), gui.entypo.back_in_time, .{}, .{}, .{
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 20, .h = 20 },
            .id_extra = 51,
        })) {
            reg.setOverride(entry.desc.id, null);
            setDirty(true);
        }
    }
}

/// One binding: click to replace, click the `x` to remove. `index` is local
/// to this row (each row is its own dvui id scope via `drawRow`'s `id_extra`).
fn drawBindingChip(command_id: []const u8, b: editor.shortcuts.Binding, index: usize) void {
    var chip = gui.box(@src(), .{ .dir = .horizontal }, .{ .id_extra = index });
    defer chip.deinit();

    const text = b.formatAlloc(gui.currentWindow().arena()) catch return;
    if (gui.button(@src(), Shortcuts.titleCase(text), .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 90 } })) {
        capturing = .{ .id = command_id, .index = index };
    }
    if (gui.buttonIcon(@src(), tr("Remove"), gui.entypo.cross, .{}, .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 16, .h = 16 },
    })) {
        Shortcuts.registry().removeBindingAt(command_id, index);
        setDirty(true);
    }
}

fn hasConflict(id: []const u8, conflicts: []const editor.shortcuts.Conflict) ?[]const u8 {
    for (conflicts) |c| {
        if (std.mem.eql(u8, c.a, id)) return c.b;
        if (std.mem.eql(u8, c.b, id)) return c.a;
    }
    return null;
}

fn bindingsSearchText(bindings: []const editor.shortcuts.Binding) []const u8 {
    if (bindings.len == 0) return "";
    const arena = gui.currentWindow().arena();
    var out: std.ArrayList(u8) = .empty;
    for (bindings, 0..) |b, i| {
        if (i > 0) out.appendSlice(arena, " ") catch return "";
        const text = b.formatAlloc(arena) catch return "";
        out.appendSlice(arena, text) catch return "";
    }
    return out.items;
}

/// Reverts every in-memory override to whatever's currently on disk,
/// discarding any unsaved rebinds. Reachable from the "..." dock menu
/// (`SettingsEditor.drawDockMenu`).
pub fn reload() void {
    if (!EditorState.settingsReady()) return;
    Shortcuts.registry().loadOverrides(&EditorState.settings);
    setDirty(false);
}

/// Clears every command's override back to its code default (in memory —
/// still requires Save to persist). Reachable from the "..." dock menu.
pub fn resetAll() void {
    const reg = Shortcuts.registry();
    for (reg.commands()) |entry| reg.setOverride(entry.desc.id, null);
    setDirty(true);
}

/// Persists every command's override state. Also called by
/// `Documents.saveOne` when the shared Settings document is saved/closed,
/// since a rebind can happen without touching a `StudioSettings` field.
pub fn save() void {
    if (!EditorState.settingsReady()) return;
    Shortcuts.registry().saveOverrides(&EditorState.settings) catch {};
    EditorState.settings.save(gui.io);
    setDirty(false);
}

/// Modal "press a key" prompt. Escape cancels; Backspace removes the slot
/// being replaced (a no-op when adding a new one); any other key becomes
/// the binding. Chords aren't capturable yet (see `Binding.second`). A
/// no-op when nothing is being captured — safe to call unconditionally.
pub fn drawCaptureDialog() void {
    const target = capturing orelse return;

    var win = gui.floatingWindow(@src(), .{
        .modal = true,
        .center_on = gui.currentWindow().subwindows.current_rect,
        .window_avoid = .nudge,
    }, .{ .role = .dialog, .min_size_content = .{ .w = 320 } });
    defer win.deinit();

    var open_flag = true;
    win.dragAreaSet(gui.windowHeader(tr("Press a Key"), "", &open_flag));
    if (!open_flag) {
        capturing = null;
        return;
    }

    gui.label(@src(), "{s}", .{tr("Press a key combination for this shortcut.")}, .{ .padding = .all(8) });
    gui.label(@src(), "{s}", .{tr("Esc to cancel, Backspace to unbind.")}, .{ .padding = .{ .x = 8, .y = 4 } });

    for (gui.events()) |*e| {
        if (e.evt != .key) continue;
        const ke = e.evt.key;
        if (ke.action != .down) continue;

        switch (ke.code) {
            .escape => {
                e.handle(@src(), win.data());
                capturing = null;
                return;
            },
            .backspace => {
                e.handle(@src(), win.data());
                if (target.index) |idx| {
                    Shortcuts.registry().removeBindingAt(target.id, idx);
                    setDirty(true);
                }
                capturing = null;
                return;
            },
            .left_shift, .right_shift, .left_control, .right_control, .left_alt, .right_alt, .left_command, .right_command => {},
            else => {
                e.handle(@src(), win.data());
                const b = editor.shortcuts.Binding.single(Shortcuts.strokeFromEvent(ke));
                if (target.index) |idx| {
                    Shortcuts.registry().replaceBindingAt(target.id, idx, b);
                } else {
                    Shortcuts.registry().addBinding(target.id, b) catch {};
                }
                setDirty(true);
                capturing = null;
                return;
            },
        }
    }
}
