const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const ProjectOps = @import("ProjectOps.zig");
const Tasks = @import("Tasks.zig");
const PlayMode = @import("PlayMode.zig");
const build_options = @import("turian_build_options");

const AboutInfo = struct {
    const name = "Turian Studio";
    const version = build_options.version;
    const authors = "Bruno Massa";
    const license = "GPL v3";
    const description = "A Zig game engine editor.";

    const dialog_message = std.fmt.comptimePrint(
        "{s}\nv{s}\n\n{s}\n\nAuthors: {s}\nLicense: {s}",
        .{ name, version, description, authors, license },
    );
};

fn projectDirExists(path: []const u8) bool {
    var d = std.Io.Dir.cwd().openDir(gui.io, path, .{}) catch return false;
    d.close(gui.io);
    return true;
}

/// Draw the main menu bar with File, Scene, Build, and Help menus.
pub fn draw(should_quit: *bool) void {
    var m = gui.menu(@src(), .horizontal, .{});
    defer m.deinit();

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
            // Runs on a worker thread; progress shows in the bottom task bar.
            Tasks.launchBuild(gui.io);
        }
    }

    if (gui.menuItemLabel(@src(), "Assets", .{ .submenu = true }, .{})) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (gui.menuItemLabel(@src(), "Reimport All", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            // Runs on a worker thread; progress shows in the bottom task bar.
            Tasks.launchReimport(gui.io);
        }

        if (gui.menuItemLabel(@src(), "Clear Asset Cache", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            if (EditorState.project_path) |p| {
                editor.asset_cache.clearAll(gui.io, p);
            }
        }
    }

    // Centered Play transport (like most engines): spacers on both sides push
    // the play controls to the middle of the menu bar.
    _ = gui.spacer(@src(), .{ .expand = .horizontal });
    drawPlayControls();
    _ = gui.spacer(@src(), .{ .expand = .horizontal, .id_extra = 1 });

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

    // Current project dropdown — shows recent projects and allows quick switching.
    {
        const proj_name = if (EditorState.project_path) |p| std.fs.path.basename(p) else "No Project";
        if (gui.menuItemLabel(@src(), proj_name, .{ .submenu = true }, .{})) |r| {
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
}

/// Play transport embedded in the menu bar: Play / Pause / Resume / Step /
/// Stop, plus "Play First Scene" (runs the project's configured first scene
/// regardless of which scene is open), and a live FPS readout while running.
fn drawPlayControls() void {
    switch (PlayMode.state()) {
        .edit => {
            // Playing the *current* scene only makes sense when one is open.
            const can_play = EditorState.hasOpenScene();
            if (gui.button(@src(), "Play", .{ .grayed = !can_play }, .{ .style = .highlight, .gravity_y = 0.5 }) and can_play)
                PlayMode.play(gui.io);
            if (gui.button(@src(), "Play First Scene", .{}, .{ .gravity_y = 0.5 }))
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

    if (PlayMode.state() != .edit) {
        gui.label(@src(), "{d:.0} FPS", .{PlayMode.fps()}, .{
            .gravity_y = 0.5,
            .padding = .{ .x = 8, .y = 2, .w = 8, .h = 2 },
            .font = .theme(.heading),
        });
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
