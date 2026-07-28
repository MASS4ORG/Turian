//! Studio wiring for `editor.session.project`: supplies dvui's `io` and
//! per-frame arena, resets the asset watcher, and hosts the native
//! folder-picker dialog. All state mutation lives in the editor module.

const gui = @import("gui");
const editor = @import("editor");
const ProjectSession = editor.session.project;
const AssetWatcher = editor.asset_watcher;
const Documents = @import("../main-window/Documents.zig");
const StudioLocale = @import("StudioLocale.zig");
const tr = StudioLocale.tr;
const build_options = @import("turian_build_options");

/// Open an existing project at the given filesystem path.
pub fn openProject(path: []const u8) void {
    AssetWatcher.reset();
    ProjectSession.openProject(gui.io, gui.currentWindow().arena(), path, Documents.restore);
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
