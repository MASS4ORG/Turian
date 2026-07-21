const std = @import("std");
const gui = @import("gui");
const EditorState = @import("EditorState.zig");
const AssetWatcher = @import("../asset-browser/AssetWatcher.zig");
const Documents = @import("../main-window/Documents.zig");
const editor = @import("editor");
const StudioLocale = @import("StudioLocale.zig");
const tr = StudioLocale.tr;

/// Open an existing project at the given filesystem path.
pub fn openProject(path: []const u8) void {
    EditorState.setProjectPath(path);
    AssetWatcher.reset();
    const result = editor.project_ops.openProject(gui.io, gui.currentWindow().arena(), path);
    EditorState.current_project = result.project;

    if (EditorState.settingsReady()) {
        const arena = gui.currentWindow().arena();
        editor.recent_projects.push(&EditorState.settings, gui.io, arena, path);
        EditorState.settings.save(gui.io);
    }

    // Start from a clean scene now; the asset scan/cook pass runs in the
    // background (see `refreshComponentsAsync`) so this window can present
    // instead of blocking on a full project import. Previously-open document
    // tabs restore once that import lands (their assets need to be resolvable
    // first — restoring a scene tab with an unimported mesh would fail to
    // load it and never retry).
    EditorState.clearScene();
    EditorState.refreshComponentsAsync(gui.io, gui.currentWindow().arena(), Documents.restore);
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
    editor.project_ops.newProject(gui.io, path, proj_name);
    openProject(path);

    if (EditorState.current_project) |*p| {
        p.setName(proj_name);
    } else {
        var proj = EditorState.Project{};
        proj.setName(proj_name);
        EditorState.current_project = proj;
    }
}

/// Save the current scene to a .zon file at the given path.
pub fn saveScene(path: []const u8) void {
    editor.scene_io.saveScene(
        gui.io,
        path,
        &EditorState.objects,
        EditorState.object_count,
        gui.currentWindow().arena(),
    );
    EditorState.markSceneSaved();
}

/// Load a scene from a .zon file and replace the current scene.
// Load scratch, kept out of the stack: a `[MAX_OBJECTS]SceneNode` local
// overflows now that the per-slot material table enlarged each node.
var load_scratch: [EditorState.MAX_OBJECTS]EditorState.SceneNode = undefined;

pub fn loadScene(path: []const u8) bool {
    var tmp_count: usize = 0;

    if (!editor.scene_io.loadScene(gui.io, gui.currentWindow().arena(), path, &load_scratch, &tmp_count)) {
        return false;
    }

    EditorState.object_count = 0;
    EditorState.selected_object = null;
    EditorState.clearUndoStack();
    for (load_scratch[0..tmp_count], 0..) |obj, i| {
        EditorState.objects[i] = obj;
    }
    EditorState.object_count = tmp_count;
    EditorState.syncSceneWithDefinitions();
    // Pull in any source-prefab edits made since this scene was saved.
    EditorState.resyncPrefabInstances(gui.io);
    EditorState.setCurrentScenePath(path);
    EditorState.markSceneSaved();

    // Auto-migrate pre-v2 scenes: rebuild model material tables by slot (v1
    // bound them per submesh). Leaves the scene dirty so a save persists v2.
    if (EditorState.assetDbReady()) {
        if (EditorState.project_path) |proj| {
            if (sceneFileVersion(path) < 2) {
                const migrated = editor.model_materials.migrateSceneMaterials(
                    gui.io,
                    std.heap.page_allocator,
                    &EditorState.asset_db,
                    proj,
                    &EditorState.objects,
                    EditorState.object_count,
                );
                if (migrated > 0) EditorState.scene_dirty = true;
            }
        }
    }
    return true;
}

/// Scene-file format version, or the current version on any read error (so a
/// failed read never triggers a spurious migration).
fn sceneFileVersion(path: []const u8) u32 {
    const current = editor.scene_io.CURRENT_VERSION;
    var file = std.Io.Dir.cwd().openFile(gui.io, path, .{}) catch return current;
    defer file.close(gui.io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(gui.io, &fbuf);
    const bytes = reader.interface.allocRemaining(gui.currentWindow().arena(), .unlimited) catch return current;
    return editor.scene_io.parseSceneVersion(bytes);
}
