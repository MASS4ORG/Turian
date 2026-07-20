const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const ProjectOps = @import("../services/ProjectOps.zig");
const Tasks = @import("Tasks.zig");
const PlayMode = @import("../scene-view/PlayMode.zig");
const Screenshots = @import("../services/Screenshots.zig");
const Documents = @import("Documents.zig");
const ProjectDropdown = @import("ProjectDropdown.zig");
const Panels = @import("Panels.zig");
const LayoutStore = @import("../services/LayoutStore.zig");
const LayoutPresets = @import("../services/LayoutPresets.zig");
const ThemeMenu = @import("ThemeMenu.zig");
const build_options = @import("turian_build_options");
const Icon = @import("../Icon.zig");
const MenuItems = @import("../MenuItems.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const Shortcuts = @import("../services/Shortcuts.zig");
const tr = StudioLocale.tr;

const AboutInfo = struct {
    const name = "Turian Studio";
    const version = build_options.version;
    const authors = "Bruno Massa";
    const license = "MPL v2";

    /// Built at click time (not comptime) so `tr()` can localize the prose.
    fn dialogMessage() []const u8 {
        const arena = gui.currentWindow().arena();
        return std.fmt.allocPrint(arena, "{s}\nv{s}\n\n{s}\n\n{s} {s}\n{s} {s}", .{
            name,
            version,
            tr("A Zig game engine editor."),
            tr("Authors:"),
            authors,
            tr("License:"),
            license,
        }) catch name;
    }
};

/// Custom About-dialog display, mirroring `gui.dialogDisplay` but with the
/// Turian logo shown above the message text.
fn aboutDialogDisplay(id: gui.Id) anyerror!void {
    const modal = gui.dataGet(null, id, "_modal", bool) orelse {
        gui.dialogRemove(id);
        return;
    };
    const title = gui.dataGetSlice(null, id, "_title", []u8) orelse {
        gui.dialogRemove(id);
        return;
    };
    const message = gui.dataGetSlice(null, id, "_message", []u8) orelse {
        gui.dialogRemove(id);
        return;
    };
    const ok_label = gui.dataGetSlice(null, id, "_ok_label", []u8) orelse {
        gui.dialogRemove(id);
        return;
    };
    const center_on = gui.dataGet(null, id, "_center_on", gui.Rect.Natural) orelse gui.currentWindow().subwindows.current_rect;
    const default = gui.dataGet(null, id, "_default", gui.enums.DialogResponse);

    var win = gui.floatingWindow(@src(), .{ .modal = modal, .center_on = center_on, .window_avoid = .nudge }, .{ .role = .dialog, .id_extra = id.asUsize() });
    defer win.deinit();

    var header_openflag = true;
    win.dragAreaSet(gui.windowHeader(title, "", &header_openflag));
    if (!header_openflag) {
        gui.dialogRemove(id);
        return;
    }

    {
        var hbox = gui.box(@src(), .{ .dir = .horizontal }, .{ .gravity_x = 1.0, .gravity_y = 1.0, .margin = gui.Rect.all(4) });
        defer hbox.deinit();

        var ok_data: gui.WidgetData = undefined;
        if (gui.button(@src(), ok_label, .{}, .{ .tab_index = 2, .data_out = &ok_data })) {
            gui.dialogRemove(id);
            return;
        }
        if (default != null and gui.firstFrame(hbox.data().id) and default.? == .ok) {
            gui.focusWidget(ok_data.id, null, null);
        }
    }

    var scroll = gui.scrollArea(@src(), .{}, .{ .expand = .both, .style = .window });
    defer scroll.deinit();

    _ = gui.image(@src(), .{
        .source = .{ .imageFile = .{ .bytes = Icon.png, .name = "turian_icon" } },
        .shrink = .ratio,
    }, .{ .min_size_content = .{ .w = 96, .h = 96 }, .gravity_x = 0.5, .margin = gui.Rect.all(4) });

    var tl = gui.textLayout(@src(), .{}, .{ .background = false });
    tl.addText(message, .{});
    tl.deinit();
}

var hamburger_open: bool = false;

/// Editor-FPS display toggle (Turian ▸ Settings ▸ General). Distinct from the
/// FPS shown while Play Mode is running (`PlayMode.fps()`, the *game's* FPS)
/// — this is the Studio UI's own frame rate, useful when diagnosing an editor
/// slowdown (e.g. a heavy asset folder) independent of whether the game is
/// playing. Persisted across sessions via the editor Settings store; `pub`
/// so `SettingsEditor.save()` can push a live change immediately (otherwise
/// the toggle wouldn't take effect until restart, since this var is only
/// ever synced *from* settings once, at startup).
pub var show_editor_fps: bool = false;
/// Settings key for `show_editor_fps`. Lazily synced on first ready frame
/// (`syncFpsFromSettings`) because settings aren't loaded when this module's
/// globals initialize.
const FPS_SETTING_KEY = "editor.show_fps";
var fps_setting_loaded: bool = false;

/// Load the persisted FPS-toggle state once settings are available. A no-op
/// after the first successful sync (and until then the default `false` shows).
fn syncFpsFromSettings() void {
    if (fps_setting_loaded or !EditorState.settingsReady()) return;
    show_editor_fps = EditorState.settings.getBool(FPS_SETTING_KEY, false);
    fps_setting_loaded = true;
}

/// Draw the main menu bar.  A hamburger icon toggles the main menu items
/// (File, Edit, Project, View, Turian) in the bar horizontally; clicking
/// outside the menu area collapses them back.
pub fn draw(should_quit: *bool) void {
    syncFpsFromSettings();

    var m = gui.menu(@src(), .horizontal, .{ .expand = .horizontal });
    defer m.deinit();

    if (gui.menuItemIcon(@src(), "hamburger", gui.entypo.menu, .{}, .{})) |_| {
        hamburger_open = !hamburger_open;
    }

    if (hamburger_open) {
        if (gui.menuItemLabel(@src(), tr("File"), .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), tr("New Project..."), .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                newProjectDialog();
            }

            if (MenuItems.command(@src(), tr("Open Project..."), "project.openProject", .{ .expand = .horizontal })) {
                m.close();
                ProjectOps.openProjectDialog();
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (MenuItems.command(@src(), tr("Save"), "file.save", .{ .expand = .horizontal })) {
                m.close();
                Documents.saveActive();
            }
            if (MenuItems.command(@src(), tr("Save All"), "file.saveAll", .{ .expand = .horizontal })) {
                m.close();
                Documents.saveAll();
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (MenuItems.command(@src(), tr("Exit"), "file.exit", .{ .expand = .horizontal })) {
                should_quit.* = true;
            }
        }

        if (gui.menuItemLabel(@src(), tr("Edit"), .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            const undo_str = if (EditorState.canUndo())
                StudioLocale.trArgs("Undo  {label}", &.{.{ .name = "label", .value = .{ .text = EditorState.undoLabel().? } }})
            else
                tr("Undo");

            const redo_str = if (EditorState.canRedo())
                StudioLocale.trArgs("Redo  {label}", &.{.{ .name = "label", .value = .{ .text = EditorState.redoLabel().? } }})
            else
                tr("Redo");

            if (MenuItems.command(@src(), undo_str, "edit.undo", .{ .expand = .horizontal }) and EditorState.canUndo()) {
                m.close();
                EditorState.undo();
            }

            if (MenuItems.command(@src(), redo_str, "edit.redo", .{ .expand = .horizontal }) and EditorState.canRedo()) {
                m.close();
                EditorState.redo();
            }
        }

        if (gui.menuItemLabel(@src(), tr("Project"), .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (MenuItems.command(@src(), tr("Build Game"), "project.buildGame", .{ .expand = .horizontal })) {
                m.close();
                Tasks.launchBuild(gui.io);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (MenuItems.command(@src(), tr("Reimport All"), "project.reimportAll", .{ .expand = .horizontal })) {
                m.close();
                Tasks.launchReimport(gui.io);
            }

            if (gui.menuItemLabel(@src(), tr("Clear Asset Cache"), .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                if (EditorState.project_path) |p| {
                    editor.asset_cache.clearAll(gui.io, p);
                }
            }
        }

        var view_open = false;
        if (gui.menuItemLabel(@src(), tr("View"), .{ .submenu = true }, .{})) |r| {
            view_open = true;
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            const l = LayoutStore.get();
            if (Panels.drawAddPanelMenuItems(l, l.firstLeaf(l.root), LayoutStore.allows)) {
                m.close();
                LayoutStore.save(gui.io);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            drawLayoutMenu(m);

            if (gui.menuItemLabel(@src(), tr("Reset Layout"), .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                LayoutStore.reset(gui.io);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (MenuItems.command(@src(), tr("Capture Screenshot"), "view.captureScreenshot", .{ .expand = .horizontal })) {
                m.close();
                _ = Screenshots.capture();
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            ThemeMenu.draw(m);
        }
        // Runs every frame the View menu is NOT open this frame — the only
        // way to catch "closed the Theme submenu without picking" (Escape,
        // click elsewhere), which `ThemeMenu.draw` itself cannot detect since
        // it simply doesn't run on that frame. See its doc comment.
        if (!view_open) ThemeMenu.revertIfViewClosed();

        if (gui.menuItemLabel(@src(), "Turian", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (!EditorState.settingsReady()) {
                gui.label(@src(), "{s}", .{tr("Settings not ready")}, .{ .expand = .horizontal, .padding = .all(8) });
            } else if (MenuItems.command(@src(), tr("Settings"), "file.openSettings", .{ .expand = .horizontal })) {
                m.close();
                Documents.openAsset(EditorState.settings.global_path, .studio_settings);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), tr("About"), .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                gui.dialog(@src(), .{}, .{
                    .title = AboutInfo.name,
                    .message = AboutInfo.dialogMessage(),
                    .displayFn = aboutDialogDisplay,
                });
            }
        }
    }

    ProjectDropdown.draw(m);

    _ = gui.spacer(@src(), .{ .expand = .horizontal });
    drawPlayControls();

    if (hamburger_open) {
        for (gui.events()) |*e| {
            switch (e.evt) {
                .mouse => |me| {
                    if ((me.action == .press or me.action == .release) and !e.handled) {
                        hamburger_open = false;
                    }
                },
                else => {},
            }
            if (!hamburger_open) break;
        }
    }
}

/// View ▸ Layout submenu: built-in presets, then any user-saved presets,
/// then a one-click "save current" action. Auto-names saved
/// presets ("Custom Layout", "Custom Layout 2", ...) rather than prompting
/// for a name — this codebase's existing naming convention is
/// create-then-rename (see `AssetActions.uniqueDirPath`'s "New Folder"),
/// not a modal text-entry dialog, and there's no persistent row here to
/// rename in place the way a grid tile or tree row would offer.
fn drawLayoutMenu(m: *gui.MenuWidget) void {
    if (LayoutStore.hasAssetContext()) return;
    if (MenuItems.submenu(@src(), tr("Layout"), .{ .expand = .horizontal })) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        for (LayoutPresets.builtins, 0..) |preset, i| {
            if (gui.menuItemLabel(@src(), preset.name, .{}, .{ .expand = .horizontal, .id_extra = i }) != null) {
                m.close();
                const built = preset.build(std.heap.page_allocator) catch {
                    gui.toast(@src(), .{ .message = tr("Failed to build layout preset") });
                    return;
                };
                LayoutStore.replace(built, gui.io);
            }
        }

        const arena = gui.currentWindow().arena();
        const custom = LayoutStore.listPresetNames(arena, gui.io);
        if (custom.len > 0) {
            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
            for (custom, 0..) |name, i| {
                if (gui.menuItemLabel(@src(), name, .{}, .{ .expand = .horizontal, .id_extra = i + 100 }) != null) {
                    m.close();
                    if (LayoutStore.loadPreset(name, std.heap.page_allocator, gui.io)) |loaded| {
                        LayoutStore.replace(loaded, gui.io);
                    } else {
                        gui.toast(@src(), .{ .message = tr("Failed to load layout preset") });
                    }
                }
            }
        }

        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

        if (gui.menuItemLabel(@src(), tr("Save Current as Preset"), .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            const name = LayoutStore.uniquePresetName(arena, gui.io);
            LayoutStore.savePreset(name, gui.io);
            const msg = StudioLocale.trArgs("Saved layout as '{name}'", &.{.{ .name = "name", .value = .{ .text = name } }});
            gui.toast(@src(), .{ .message = msg });
        }
    }
}

/// Play transport embedded in the menu bar: Play / Pause / Resume / Step /
/// Stop, plus "Play First Scene" (runs the project's configured first scene
/// regardless of which scene is open), and a live FPS readout while running.
fn drawPlayControls() void {
    if (PlayMode.state() != .edit) {
        gui.label(@src(), "{d:.0} FPS", .{PlayMode.fps()}, .{
            .gravity_y = 0.5,
            .padding = .{ .x = 8, .y = 2, .w = 8, .h = 2 },
            .font = .theme(.heading),
        });
    } else if (show_editor_fps) {
        // The Studio UI's own frame rate (not the game's) — see
        // `show_editor_fps`'s doc comment.
        gui.label(@src(), "{d:.0} FPS", .{EditorState.debug_metrics.fps}, .{
            .gravity_y = 0.5,
            .padding = .{ .x = 8, .y = 2, .w = 8, .h = 2 },
            .font = .theme(.heading),
        });
    }
    switch (PlayMode.state()) {
        .edit => {
            // Playing the *current* scene only makes sense when one is open.
            const can_play = EditorState.hasOpenScene();
            if (transportButton(.play, !can_play) and can_play) PlayMode.play(gui.io);
            if (transportButton(.play_global, false)) PlayMode.playFirstScene(gui.io);
        },
        .playing => {
            if (transportButton(.pause, false)) PlayMode.pause();
            if (transportButton(.stop, false)) PlayMode.stop();
        },
        .paused => {
            if (transportButton(.play, false)) PlayMode.play(gui.io);
            if (transportButton(.step, false)) PlayMode.step();
            if (transportButton(.stop, false)) PlayMode.stop();
        },
    }
}

/// One transport action. The bar is icon-only, so shape and tint are all a
/// reader has to go on — hence a distinct color per action rather than five
/// identical gray glyphs.
///
/// Play Global (run the project's configured first scene, whatever scene is
/// open for editing) has no icon of its own in any icon set, and neither
/// entypo nor dvui can compose one glyph over another — an icon is a single
/// pre-baked TVG path, with no layering, badge or background slot. So the
/// meaning is carried by a glyph that already says it: `jump_to_start` — start
/// from the beginning, not from here.
const Transport = enum {
    play,
    play_global,
    pause,
    step,
    stop,

    fn icon(self: Transport) []const u8 {
        return switch (self) {
            .play => gui.entypo.controller_play,
            .play_global => gui.entypo.controller_jump_to_start,
            .pause => gui.entypo.controller_pause,
            .step => gui.entypo.controller_next,
            .stop => gui.entypo.controller_stop,
        };
    }

    fn tip(self: Transport) []const u8 {
        return switch (self) {
            .play => playStopTip(tr("Play the open scene")),
            .play_global => playFirstSceneTip(),
            .pause => tr("Pause"),
            .step => tr("Step one frame"),
            .stop => playStopTip(tr("Stop")),
        };
    }

    fn playStopTip(base: []const u8) []const u8 {
        return withShortcutSuffix(base, "play.toggle");
    }

    fn playFirstSceneTip() []const u8 {
        return withShortcutSuffix(tr("Play from the project's first scene"), "play.firstScene");
    }

    /// Appends `command_id`'s live shortcut label, e.g. "Play the open
    /// scene  (Ctrl+P)".
    fn withShortcutSuffix(base: []const u8, command_id: []const u8) []const u8 {
        const shortcut = Shortcuts.label(command_id);
        if (shortcut.len == 0) return base;
        return std.fmt.allocPrint(gui.currentWindow().arena(), "{s}  ({s})", .{ base, shortcut }) catch base;
    }

    fn color(self: Transport) gui.Color {
        return switch (self) {
            .play => .{ .r = 0x5c, .g = 0xc8, .b = 0x6b },
            .play_global => .{ .r = 0x4a, .g = 0x9e, .b = 0xff },
            .pause => .{ .r = 0xe0, .g = 0xb0, .b = 0x30 },
            .step => .{ .r = 0xc8, .g = 0xc8, .b = 0xc8 },
            .stop => .{ .r = 0xe0, .g = 0x50, .b = 0x50 },
        };
    }
};

fn transportButton(action: Transport, grayed: bool) bool {
    const id: usize = @intFromEnum(action);
    const tint = action.color();

    var wd: gui.WidgetData = undefined;
    const clicked = gui.buttonIcon(@src(), action.tip(), action.icon(), .{ .grayed = grayed }, .{}, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 18, .h = 18 },
        .padding = .all(4),
        .margin = .{ .x = 2 },
        .color_text = if (grayed) tint.opacity(0.35) else tint,
        .data_out = &wd,
    });
    gui.tooltip(@src(), .{ .active_rect = wd.rectScale().r }, "{s}", .{action.tip()}, .{ .id_extra = id });
    return clicked;
}

fn newProjectDialog() void {
    if (!gui.useTinyFileDialogs) {
        gui.dialog(@src(), .{}, .{
            .title = tr("Not Available"),
            .message = tr("Native file dialogs are not enabled in this build."),
        });
        return;
    }

    const path = gui.dialogNativeFolderSelect(gui.currentWindow().arena(), .{
        .title = tr("Choose New Project Folder"),
    }) catch |err| blk: {
        gui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        const proj_name = std.fs.path.basename(p);
        ProjectOps.newProject(p, if (proj_name.len > 0) proj_name else tr("New Project"));
        EditorState.initDefaultScene(gui.io);
    }
}
