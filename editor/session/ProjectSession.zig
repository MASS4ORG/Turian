//! Session-level project and scene operations: opens/creates a project and saves/loads scenes *into* the live `EditorState`. GUI-free — the host supplies `io`, a per-call arena, and optional completion callbacks.

const std = @import("std");
const engine = @import("engine");
const editor = @import("../root.zig");
const EditorState = @import("EditorState.zig");

/// Open an existing project at the given filesystem path. The asset scan/cook pass runs in the background (`refreshComponentsAsync`) to avoid blocking on a full project import; `on_import_done` fires once the import lands.
pub fn openProject(
    io: std.Io,
    arena: std.mem.Allocator,
    path: []const u8,
    on_import_done: ?*const fn () void,
) void {
    EditorState.setProjectPath(path);
    const result = editor.project_ops.openProject(io, arena, path);
    EditorState.current_project = result.project;

    if (EditorState.settingsReady()) {
        editor.recent_projects.push(&EditorState.settings, io, arena, path);
        EditorState.settings.save(io);
    }

    EditorState.clearScene();
    EditorState.refreshComponentsAsync(io, arena, on_import_done);
}

/// Create a new project at `path` named `proj_name`, then open it.
pub fn newProject(
    io: std.Io,
    arena: std.mem.Allocator,
    path: []const u8,
    proj_name: []const u8,
    engine_version: []const u8,
    on_import_done: ?*const fn () void,
) void {
    editor.project_ops.newProject(io, path, proj_name, engine_version);
    openProject(io, arena, path, on_import_done);

    if (EditorState.current_project) |*p| {
        p.setName(proj_name);
    } else {
        var proj = EditorState.Project{};
        proj.setName(proj_name);
        EditorState.current_project = proj;
    }
}

/// Save the current scene to `path`.
pub fn saveScene(io: std.Io, arena: std.mem.Allocator, path: []const u8) void {
    editor.scene_io.saveScene(io, path, EditorState.objects, EditorState.object_count, arena);
    EditorState.markSceneSaved();
}

// Load scratch, kept off the stack: `[MAX_OBJECTS]SceneNode` local overflows when the per-slot material table enlarges each node. Grown on demand via `EditorState.gpa` (persistent, cached across calls). `scene_io.loadScene` reports the true node count in `tmp_count` so a large scene loads completely.
var load_scratch: []EditorState.SceneNode = &.{};

/// Load a scene from `path`, replacing the current one. Returns false if the
/// file could not be read or the scene exceeds the node growth ceiling.
pub fn loadScene(io: std.Io, arena: std.mem.Allocator, path: []const u8) bool {
    if (load_scratch.len == 0) {
        load_scratch = EditorState.gpa.alloc(EditorState.SceneNode, EditorState.MAX_OBJECTS) catch return false;
    }
    var tmp_count: usize = 0;
    while (true) {
        if (!editor.scene_io.loadScene(io, arena, path, load_scratch, &tmp_count)) {
            return false;
        }
        if (tmp_count <= load_scratch.len or load_scratch.len >= engine.scene.GROWTH_CEILING) break;
        const new_cap = @min(tmp_count, engine.scene.GROWTH_CEILING);
        const grown = EditorState.gpa.realloc(load_scratch, new_cap) catch break;
        load_scratch = grown;
    }
    if (tmp_count > load_scratch.len) return false; // hit the growth ceiling

    EditorState.object_count = 0;
    EditorState.selected_object = null;
    EditorState.clearUndoStack();
    EditorState.ensureObjectCapacity(tmp_count);
    if (tmp_count > EditorState.objects.len) return false; // hit the growth ceiling
    for (load_scratch[0..tmp_count], 0..) |obj, i| {
        EditorState.objects[i] = obj;
    }
    EditorState.object_count = tmp_count;
    EditorState.syncSceneWithDefinitions();
    // Pull in any source-prefab edits made since this scene was saved.
    EditorState.resyncPrefabInstances(io);
    EditorState.setCurrentScenePath(path);
    EditorState.markSceneSaved();

    // Auto-migrate scenes with per-submesh material binding to per-slot tables. Leaves the scene dirty so a save persists the conversion.
    if (EditorState.assetDbReady()) {
        if (EditorState.project_path) |proj| {
            if (sceneFileVersion(io, arena, path) < 2) {
                const migrated = editor.model_materials.migrateSceneMaterials(
                    io,
                    std.heap.page_allocator,
                    &EditorState.asset_db,
                    proj,
                    EditorState.objects,
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
fn sceneFileVersion(io: std.Io, arena: std.mem.Allocator, path: []const u8) u32 {
    const current = editor.scene_io.CURRENT_VERSION;
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return current;
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const bytes = reader.interface.allocRemaining(arena, .unlimited) catch return current;
    return editor.scene_io.parseSceneVersion(bytes);
}
