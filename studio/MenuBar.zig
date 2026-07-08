const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const ProjectOps = @import("ProjectOps.zig");
const Tasks = @import("Tasks.zig");
const PlayMode = @import("PlayMode.zig");
const ProfilerPanel = @import("ProfilerPanel.zig");
const Screenshots = @import("Screenshots.zig");
const build_options = @import("turian_build_options");

const AboutInfo = struct {
    const name = "Turian Studio";
    const version = build_options.version;
    const authors = "Bruno Massa";
    const license = "MPL v2";
    const description = "A Zig game engine editor.";

    const dialog_message = std.fmt.comptimePrint(
        "{s}\nv{s}\n\n{s}\n\nAuthors: {s}\nLicense: {s}",
        .{ name, version, description, authors, license },
    );
};

var hamburger_open: bool = false;

/// Editor-FPS display toggle (View menu). Distinct from the FPS shown while
/// Play Mode is running (`PlayMode.fps()`, the *game's* FPS) — this is the
/// Studio UI's own frame rate, useful when diagnosing an editor slowdown
/// (e.g. a heavy asset folder) independent of whether the game is playing.
/// Persisted across sessions via the editor Settings store.
var show_editor_fps: bool = false;
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

fn projectDirExists(path: []const u8) bool {
    var d = std.Io.Dir.cwd().openDir(gui.io, path, .{}) catch return false;
    d.close(gui.io);
    return true;
}

/// Draw the main menu bar.  A hamburger icon toggles the main menu items
/// (File, Edit, Scene, …) in the bar horizontally; clicking outside the
/// menu area collapses them back.
pub fn draw(should_quit: *bool) void {
    syncFpsFromSettings();

    var m = gui.menu(@src(), .horizontal, .{ .expand = .horizontal });
    defer m.deinit();

    if (gui.menuItemIcon(@src(), "hamburger", gui.entypo.menu, .{}, .{})) |_| {
        hamburger_open = !hamburger_open;
    }

    if (hamburger_open) {
        if (gui.menuItemLabel(@src(), "File", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "New Project...", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                newProjectDialog();
            }

            if (gui.menuItemLabel(@src(), "Open Project...", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                openProjectDialog();
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), "Save Scene", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                if (EditorState.current_scene_path) |path| {
                    ProjectOps.saveScene(path);
                }
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), "Exit", .{}, .{ .expand = .horizontal }) != null) {
                should_quit.* = true;
            }
        }

        if (gui.menuItemLabel(@src(), "Edit", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            var undo_buf: [128]u8 = undefined;
            const undo_str = if (EditorState.canUndo())
                std.fmt.bufPrint(&undo_buf, "Undo  {s}\tCtrl+Z", .{EditorState.undoLabel().?}) catch "Undo\tCtrl+Z"
            else
                "Undo\tCtrl+Z";

            var redo_buf: [128]u8 = undefined;
            const redo_str = if (EditorState.canRedo())
                std.fmt.bufPrint(&redo_buf, "Redo  {s}\tCtrl+Shift+Z", .{EditorState.redoLabel().?}) catch "Redo\tCtrl+Shift+Z"
            else
                "Redo\tCtrl+Shift+Z";

            const do_undo = gui.menuItemLabel(@src(), undo_str, .{}, .{ .expand = .horizontal });
            if (do_undo != null and EditorState.canUndo()) {
                m.close();
                EditorState.undo();
            }

            const do_redo = gui.menuItemLabel(@src(), redo_str, .{}, .{ .expand = .horizontal });
            if (do_redo != null and EditorState.canRedo()) {
                m.close();
                EditorState.redo();
            }
        }

        if (gui.menuItemLabel(@src(), "Scene", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "Add Empty Object", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                _ = EditorState.addObjectWithUndo(gui.frameTimeNS(), gui.io, "New Object", -1);
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), "Reset Scene", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                EditorState.initDefaultScene(gui.io);
            }
        }

        if (gui.menuItemLabel(@src(), "Build", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "Build Game", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                Tasks.launchBuild(gui.io);
            }
        }

        if (gui.menuItemLabel(@src(), "Assets", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "Reimport All", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                Tasks.launchReimport(gui.io);
            }

            if (gui.menuItemLabel(@src(), "Clear Asset Cache", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                if (EditorState.project_path) |p| {
                    editor.asset_cache.clearAll(gui.io, p);
                }
            }
        }

        if (gui.menuItemLabel(@src(), "View", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            const profiler_label = if (ProfilerPanel.isOpen()) "Hide Profiler" else "Show Profiler";
            if (gui.menuItemLabel(@src(), profiler_label, .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                ProfilerPanel.toggle();
            }

            if (gui.menuItemLabel(@src(), "Capture Screenshot", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                _ = Screenshots.capture();
            }

            const fps_label = if (show_editor_fps) "Hide Editor FPS" else "Show Editor FPS";
            if (gui.menuItemLabel(@src(), fps_label, .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                show_editor_fps = !show_editor_fps;
                if (EditorState.settingsReady()) {
                    EditorState.settings.setBool(FPS_SETTING_KEY, show_editor_fps) catch {};
                    EditorState.settings.save(gui.io);
                }
            }
        }

        if (gui.menuItemLabel(@src(), "Help", .{ .submenu = true }, .{})) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (gui.menuItemLabel(@src(), "About", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                gui.dialog(@src(), .{}, .{
                    .title = AboutInfo.name,
                    .message = AboutInfo.dialog_message,
                });
            }
        }
    }

    drawProjectDropdown(m);

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

// Current project dropdown — shows recent projects and allows quick switching.
fn drawProjectDropdown(m: *gui.MenuWidget) void {
    const proj_name = if (EditorState.project_path) |p| std.fs.path.basename(p) else "No Project";
    if (gui.menuItemLabel(@src(), proj_name, .{ .submenu = true }, .{ .font = .theme(.heading) })) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (!EditorState.settingsReady()) {
            gui.label(@src(), "Settings not ready", .{}, .{ .expand = .horizontal, .padding = .all(8) });
        } else {
            const arena = gui.currentWindow().arena();
            const recent = editor.recent_projects.list(&EditorState.settings, arena);

            if (recent.len == 0) {
                gui.label(@src(), "No recent projects", .{}, .{ .expand = .horizontal, .padding = .all(8) });
            } else {
                for (recent, 0..) |path, i| {
                    const is_current = if (EditorState.project_path) |cur|
                        std.mem.eql(u8, cur, path)
                    else
                        false;

                    const exists = projectDirExists(path);

                    const base = std.fs.path.basename(path);
                    var lbuf: [300]u8 = undefined;
                    const label = if (!exists)
                        std.fmt.bufPrint(&lbuf, "[!] {s}", .{base}) catch base
                    else if (is_current)
                        std.fmt.bufPrint(&lbuf, "* {s}", .{base}) catch base
                    else
                        base;

                    if (gui.menuItemLabel(@src(), label, .{}, .{
                        .expand = .horizontal,
                        .id_extra = i,
                    }) != null) {
                        if (exists and !is_current) {
                            m.close();
                            ProjectOps.openProject(path);
                        } else if (!exists) {
                            editor.recent_projects.remove(&EditorState.settings, gui.io, arena, path);
                            EditorState.settings.save(gui.io);
                        }
                    }
                }
            }

            _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

            if (gui.menuItemLabel(@src(), "Open Project...", .{}, .{ .expand = .horizontal }) != null) {
                m.close();
                openProjectDialog();
            }
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
            if (gui.button(@src(), "Play", .{ .grayed = !can_play }, .{ .gravity_y = 0.5 }) and can_play)
                PlayMode.play(gui.io);
            if (gui.button(@src(), "Play Global", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.playFirstScene(gui.io);
        },
        .playing => {
            if (gui.button(@src(), "Pause", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.pause();
            if (gui.button(@src(), "Stop", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.stop();
        },
        .paused => {
            if (gui.button(@src(), "Resume", .{}, .{ .style = .highlight, .gravity_y = 0.5 }))
                PlayMode.play(gui.io);
            if (gui.button(@src(), "Step", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.step();
            if (gui.button(@src(), "Stop", .{}, .{ .gravity_y = 0.5, .id_extra = 1 }))
                PlayMode.stop();
        },
    }
}

fn openProjectDialog() void {
    if (!gui.useTinyFileDialogs) {
        gui.dialog(@src(), .{}, .{
            .title = "Not Available",
            .message = "Native file dialogs are not enabled in this build.",
        });
        return;
    }

    const path = gui.dialogNativeFolderSelect(gui.currentWindow().arena(), .{
        .title = "Open Project Folder",
    }) catch |err| blk: {
        gui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        ProjectOps.openProject(p);
    }
}

fn newProjectDialog() void {
    if (!gui.useTinyFileDialogs) {
        gui.dialog(@src(), .{}, .{
            .title = "Not Available",
            .message = "Native file dialogs are not enabled in this build.",
        });
        return;
    }

    const path = gui.dialogNativeFolderSelect(gui.currentWindow().arena(), .{
        .title = "Choose New Project Folder",
    }) catch |err| blk: {
        gui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        const proj_name = std.fs.path.basename(p);
        ProjectOps.newProject(p, if (proj_name.len > 0) proj_name else "New Project");
        EditorState.initDefaultScene(gui.io);
    }
}
