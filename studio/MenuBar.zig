const std = @import("std");
const dvui = @import("dvui");
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
    var d = std.Io.Dir.cwd().openDir(dvui.io, path, .{}) catch return false;
    d.close(dvui.io);
    return true;
}

/// Draw the main menu bar with File, Scene, Build, and Help menus.
pub fn draw(should_quit: *bool) void {
    var m = dvui.menu(@src(), .horizontal, .{});
    defer m.deinit();

    if (dvui.menuItemLabel(@src(), "File", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (dvui.menuItemLabel(@src(), "New Project...", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            newProjectDialog();
        }

        if (dvui.menuItemLabel(@src(), "Open Project...", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            openProjectDialog();
        }

        _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

        if (dvui.menuItemLabel(@src(), "Save Scene", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            if (EditorState.current_scene_path) |path| {
                ProjectOps.saveScene(path);
            }
        }

        _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

        if (dvui.menuItemLabel(@src(), "Exit", .{}, .{ .expand = .horizontal }) != null) {
            should_quit.* = true;
        }
    }

    if (dvui.menuItemLabel(@src(), "Edit", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
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

        const do_undo = dvui.menuItemLabel(@src(), undo_str, .{}, .{ .expand = .horizontal });
        if (do_undo != null and EditorState.canUndo()) {
            m.close();
            EditorState.undo();
        }

        const do_redo = dvui.menuItemLabel(@src(), redo_str, .{}, .{ .expand = .horizontal });
        if (do_redo != null and EditorState.canRedo()) {
            m.close();
            EditorState.redo();
        }
    }

    if (dvui.menuItemLabel(@src(), "Scene", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (dvui.menuItemLabel(@src(), "Add Empty Object", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            _ = EditorState.addObjectWithUndo(dvui.frameTimeNS(), dvui.io, "New Object", -1);
        }

        _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

        if (dvui.menuItemLabel(@src(), "Reset Scene", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            EditorState.initDefaultScene(dvui.io);
        }
    }

    if (dvui.menuItemLabel(@src(), "Build", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (dvui.menuItemLabel(@src(), "Build Game", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            // Runs on a worker thread; progress shows in the bottom task bar.
            Tasks.launchBuild(dvui.io);
        }
    }

    if (dvui.menuItemLabel(@src(), "Assets", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (dvui.menuItemLabel(@src(), "Reimport All", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            // Runs on a worker thread; progress shows in the bottom task bar.
            Tasks.launchReimport(dvui.io);
        }

        if (dvui.menuItemLabel(@src(), "Clear Asset Cache", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            if (EditorState.project_path) |p| {
                editor.asset_cache.clearAll(dvui.io, p);
            }
        }
    }

    // Centered Play transport (like most engines): spacers on both sides push
    // the play controls to the middle of the menu bar.
    _ = dvui.spacer(@src(), .{ .expand = .horizontal });
    drawPlayControls();
    _ = dvui.spacer(@src(), .{ .expand = .horizontal, .id_extra = 1 });

    if (dvui.menuItemLabel(@src(), "Help", .{ .submenu = true }, .{})) |r| {
        var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();

        if (dvui.menuItemLabel(@src(), "About", .{}, .{ .expand = .horizontal }) != null) {
            m.close();
            dvui.dialog(@src(), .{}, .{
                .title = AboutInfo.name,
                .message = AboutInfo.dialog_message,
            });
        }
    }

    // Current project dropdown — shows recent projects and allows quick switching.
    {
        const proj_name = if (EditorState.project_path) |p| std.fs.path.basename(p) else "No Project";
        if (dvui.menuItemLabel(@src(), proj_name, .{ .submenu = true }, .{})) |r| {
            var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();

            if (!EditorState.settingsReady()) {
                dvui.label(@src(), "Settings not ready", .{}, .{ .expand = .horizontal, .padding = .all(8) });
            } else {
                const arena = dvui.currentWindow().arena();
                const recent = editor.recent_projects.list(&EditorState.settings, arena);

                if (recent.len == 0) {
                    dvui.label(@src(), "No recent projects", .{}, .{ .expand = .horizontal, .padding = .all(8) });
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

                        if (dvui.menuItemLabel(@src(), label, .{}, .{
                            .expand = .horizontal,
                            .id_extra = i,
                        }) != null) {
                            if (exists and !is_current) {
                                m.close();
                                ProjectOps.openProject(path);
                                EditorState.clearScene();
                            } else if (!exists) {
                                editor.recent_projects.remove(&EditorState.settings, dvui.io, arena, path);
                                EditorState.settings.save(dvui.io);
                            }
                        }
                    }
                }

                _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });

                if (dvui.menuItemLabel(@src(), "Open Project...", .{}, .{ .expand = .horizontal }) != null) {
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
            if (dvui.button(@src(), "Play", .{}, .{ .style = .highlight, .gravity_y = 0.5 }))
                PlayMode.play(dvui.io);
            if (dvui.button(@src(), "Play First Scene", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.playFirstScene(dvui.io);
        },
        .playing => {
            if (dvui.button(@src(), "Pause", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.pause();
            if (dvui.button(@src(), "Stop", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.stop();
        },
        .paused => {
            if (dvui.button(@src(), "Resume", .{}, .{ .style = .highlight, .gravity_y = 0.5 }))
                PlayMode.play(dvui.io);
            if (dvui.button(@src(), "Step", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.step();
            if (dvui.button(@src(), "Stop", .{}, .{ .gravity_y = 0.5, .id_extra = 1 }))
                PlayMode.stop();
        },
    }

    if (PlayMode.state() != .edit) {
        dvui.label(@src(), "{d:.0} FPS", .{PlayMode.fps()}, .{
            .gravity_y = 0.5,
            .padding = .{ .x = 8, .y = 2, .w = 8, .h = 2 },
            .font = .theme(.heading),
        });
    }
}

fn openProjectDialog() void {
    if (!dvui.useTinyFileDialogs) {
        dvui.dialog(@src(), .{}, .{
            .title = "Not Available",
            .message = "Native file dialogs are not enabled in this build.",
        });
        return;
    }

    const path = dvui.dialogNativeFolderSelect(dvui.currentWindow().arena(), .{
        .title = "Open Project Folder",
    }) catch |err| blk: {
        dvui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        ProjectOps.openProject(p);
        EditorState.clearScene();
    }
}

fn newProjectDialog() void {
    if (!dvui.useTinyFileDialogs) {
        dvui.dialog(@src(), .{}, .{
            .title = "Not Available",
            .message = "Native file dialogs are not enabled in this build.",
        });
        return;
    }

    const path = dvui.dialogNativeFolderSelect(dvui.currentWindow().arena(), .{
        .title = "Choose New Project Folder",
    }) catch |err| blk: {
        dvui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        const proj_name = std.fs.path.basename(p);
        ProjectOps.newProject(p, if (proj_name.len > 0) proj_name else "New Project");
        EditorState.initDefaultScene(dvui.io);
    }
}
