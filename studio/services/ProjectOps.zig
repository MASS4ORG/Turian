const std = @import("std");
const gui = @import("gui");
const EditorState = @import("EditorState.zig");
const AssetWatcher = @import("../asset-browser/AssetWatcher.zig");
const Documents = @import("../main-window/Documents.zig");
const editor = @import("editor");

/// Open an existing project at the given filesystem path.
pub fn openProject(path: []const u8) void {
    EditorState.setProjectPath(path);
    AssetWatcher.reset();
    const result = editor.project_ops.openProject(gui.io, gui.currentWindow().arena(), path);
    EditorState.current_project = result.project;
    EditorState.refreshComponents(gui.io, gui.currentWindow().arena());

    if (EditorState.settingsReady()) {
        const arena = gui.currentWindow().arena();
        editor.recent_projects.push(&EditorState.settings, gui.io, arena, path);
        EditorState.settings.save(gui.io);
    }

    // Start from a clean scene, then restore the document tabs that were open
    // for this project when it was last closed.
    EditorState.clearScene();
    Documents.restore();
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
pub fn loadScene(path: []const u8) bool {
    var tmp_objects: [EditorState.MAX_OBJECTS]EditorState.SceneNode = undefined;
    var tmp_count: usize = 0;

    if (!editor.scene_io.loadScene(gui.io, gui.currentWindow().arena(), path, &tmp_objects, &tmp_count)) {
        return false;
    }

    EditorState.object_count = 0;
    EditorState.selected_object = null;
    EditorState.clearUndoStack();
    for (tmp_objects[0..tmp_count], 0..) |obj, i| {
        EditorState.objects[i] = obj;
    }
    EditorState.object_count = tmp_count;
    EditorState.syncSceneWithDefinitions();
    // Pull in any source-prefab edits made since this scene was saved.
    EditorState.resyncPrefabInstances(gui.io);
    EditorState.setCurrentScenePath(path);
    EditorState.markSceneSaved();
    return true;
}
