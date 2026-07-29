//! Studio wiring for `editor.session.project`: supplies dvui's `io` and
//! per-frame arena, resets the asset watcher, and hosts the native
//! folder-picker dialog. All state mutation lives in the editor module.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const ProjectSession = editor.session.project;
const AssetWatcher = editor.asset_watcher;
const EditorState = editor.EditorState;
const Documents = @import("../main-window/Documents.zig");
const DirtyCheck = @import("DirtyCheck.zig");
const StudioLocale = @import("StudioLocale.zig");
const tr = StudioLocale.tr;
const build_options = @import("turian_build_options");

/// Open an existing project at the given filesystem path. Defers behind
/// `DirtyCheck`'s confirmation dialog if the current project has unsaved
/// changes.
pub fn openProject(path: []const u8) void {
    DirtyCheck.requestSwitchProject(path);
}

/// Opens `path` immediately, bypassing the unsaved-changes gate. Called by
/// `DirtyCheck` once a switch is confirmed (or nothing needed saving), and
/// by the `--project` CLI flag at startup, where nothing can be dirty yet.
pub fn openProjectImmediate(path: []const u8) void {
    AssetWatcher.reset();
    ProjectSession.openProject(gui.io, gui.currentWindow().arena(), path, Documents.restore);
}

/// Launches a new, independent Studio process with `path` loaded — the
/// project dropdown's "open in new window" button. Fire-and-forget: the new
/// process outlives this one and isn't waited on.
pub fn openInNewWindow(path: []const u8) void {
    var exe_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const exe_len = std.process.executablePath(gui.io, &exe_buf) catch |err| {
        gui.log.debug("Could not resolve own executable path: {any}", .{err});
        return;
    };
    const argv = [_][]const u8{ exe_buf[0..exe_len], "--project", path };
    _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch |err| {
        gui.log.debug("Could not open project in new window: {any}", .{err});
    };
}

/// Prompt for a project folder via a native dialog and open it. Shared by
/// the File menu and the project selector dropdown.
pub fn openProjectDialog() void {
    if (!gui.useTinyFileDialogs) {
        gui.dialog(@src(), .{}, .{
            .title = tr("Not Available"),
            .message = tr("Native file dialogs are not enabled in this build."),
        });
        return;
    }

    const path = gui.dialogNativeFolderSelect(gui.currentWindow().arena(), .{
        .title = tr("Open Project Folder"),
    }) catch |err| blk: {
        gui.log.debug("Could not open folder dialog: {any}", .{err});
        break :blk null;
    };

    if (path) |p| {
        openProject(p);
    }
}

/// Prompt for a new project's folder via a native dialog and create it there,
/// naming it after the chosen folder. Shared by the File menu and the
/// welcome panel.
pub fn newProjectDialog() void {
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
        newProject(p, if (proj_name.len > 0) proj_name else tr("New Project"));
        EditorState.initDefaultScene(gui.io);
    }
}

/// Create a new project at the given path with the given name.
pub fn newProject(path: []const u8, proj_name: []const u8) void {
    AssetWatcher.reset();
    ProjectSession.newProject(
        gui.io,
        gui.currentWindow().arena(),
        path,
        proj_name,
        build_options.version,
        Documents.restore,
    );
}

/// Save the current scene to a .json file at the given path.
pub fn saveScene(path: []const u8) void {
    ProjectSession.saveScene(gui.io, gui.currentWindow().arena(), path);
}

/// Load a scene from a .json file and replace the current scene.
pub fn loadScene(path: []const u8) bool {
    return ProjectSession.loadScene(gui.io, gui.currentWindow().arena(), path);
}
