const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const build_options = @import("turian_build_options");
const EditorState = @import("EditorState.zig");

pub const Vector3 = engine.Vector3;
pub const Transform = engine.Transform;
pub const Component = engine.Component;
pub const UserScriptRef = engine.UserScriptRef;
pub const SceneNode = engine.SceneNode;
pub const Project = engine.Project;
pub const MAX_OBJECTS = engine.scene.MAX_OBJECTS;
pub const NAME_MAX = engine.scene.NAME_MAX;
pub const ComponentDef = editor.ComponentDef;

pub const MAX_DISCOVERED = editor.scanner.MAX_COMPONENTS;

// ── Settings ──────────────────────────────────────────────────────────────────

var settings_initialized: bool = false;

pub fn initSettings(io: std.Io, allocator: std.mem.Allocator, global_dir: []const u8) !void {
    EditorState.settings = try editor.Settings.init(allocator, global_dir, null);
    EditorState.settings.load(io);
    settings_initialized = true;
}

pub fn deinitSettings(io: std.Io) void {
    if (!settings_initialized) return;
    EditorState.settings.save(io);
    EditorState.settings.deinit();
    settings_initialized = false;
}

pub fn settingsReady() bool {
    return settings_initialized;
}

// ── Asset Database ────────────────────────────────────────────────────────────

pub fn assetDbReady() bool {
    return EditorState.asset_db_initialized;
}

// ── Background task registry ─────────────────────────────────────────────────
// Owned here (rather than in `studio/Tasks.zig`) so this file's own background
// reflect job below can create/update tasks without a circular import — every
// other studio file already depends on `EditorState`, never the reverse.
// `Tasks.tm()` exposes this same instance to the task bar / menu.
var task_manager: editor.TaskManager = editor.TaskManager.init();

pub fn taskManager() *editor.TaskManager {
    return &task_manager;
}

// ── Reflect job state ─────────────────────────────────────────────────────

pub fn markSceneSaved() void {
    const UndoRedo = @import("UndoRedo.zig");
    EditorState.saved_undo_depth = UndoRedo.undo_len;
    EditorState.scene_dirty = false;
}

// ── Scene open state ─────────────────────────────────────────────────────────

/// True when a scene is loaded in the hierarchy (so Play / object creation make
/// sense). A scene saved to disk, a freshly-created default scene, or any scene
/// with objects all count as open.
pub fn hasOpenScene() bool {
    return EditorState.scene_open or EditorState.object_count > 0 or EditorState.current_scene_path != null;
}

pub fn setCurrentScenePath(path: []const u8) void {
    const len = @min(path.len, EditorState.current_scene_path_buf.len);
    @memcpy(EditorState.current_scene_path_buf[0..len], path[0..len]);
    EditorState.current_scene_path = EditorState.current_scene_path_buf[0..len];
    EditorState.scene_open = true;
}

pub fn clearScene() void {
    const Selection = @import("Selection.zig");
    const UndoRedo = @import("UndoRedo.zig");
    EditorState.object_count = 0;
    EditorState.selected_object = null;
    Selection.clearSelectedObjects();
    EditorState.scene_dirty = false;
    EditorState.current_scene_path = null;
    EditorState.scene_open = false;
    UndoRedo.clearUndoStack();
    EditorState.saved_undo_depth = 0;
}

pub fn initDefaultScene(io: std.Io) void {
    const Selection = @import("Selection.zig");
    const UndoRedo = @import("UndoRedo.zig");
    const SceneTreeOps = @import("SceneTreeOps.zig");
    EditorState.object_count = 0;
    EditorState.selected_object = null;
    Selection.clearSelectedObjects();
    EditorState.scene_dirty = false;
    EditorState.current_scene_path = null;
    EditorState.scene_open = true;
    UndoRedo.clearUndoStack();

    const env = SceneTreeOps.addObject(io, "Environment", -1);

    const ground = SceneTreeOps.addObject(io, "Ground", @intCast(env));
    _ = EditorState.objects[ground].addComponent(.{ .mesh_renderer = .{} });
    EditorState.objects[ground].transform.scale = .{ .x = 10, .y = 0.1, .z = 10 };

    _ = SceneTreeOps.addObject(io, "Props", @intCast(env));

    const cam = SceneTreeOps.addObject(io, "Main Camera", -1);
    _ = EditorState.objects[cam].addComponent(.{ .camera = .{} });
    EditorState.objects[cam].transform.position = .{ .x = 0, .y = 2, .z = -5 };

    const dir_light = SceneTreeOps.addObject(io, "Directional Light", -1);
    _ = EditorState.objects[dir_light].addComponent(.{ .light = .{} });
    EditorState.objects[dir_light].transform.rotation = .{ .x = 50, .y = -30, .z = 0 };
}

// ── Selected asset ───────────────────────────────────────────────────────────

pub fn selectAsset(path: []const u8) void {
    EditorState.selected_object = null;
    const len = @min(path.len, EditorState.selected_asset_path_buf.len);
    @memcpy(EditorState.selected_asset_path_buf[0..len], path[0..len]);
    EditorState.selected_asset_path_len = len;
    EditorState.selected_asset_path = EditorState.selected_asset_path_buf[0..len];
}

pub fn clearSelectedAsset() void {
    EditorState.selected_asset_path = null;
    EditorState.selected_asset_path_len = 0;
}

// ── Active asset-browser folder ──────────────────────────────────────────────

pub fn setActiveBrowseDir(path: []const u8) void {
    const len = @min(path.len, EditorState.active_browse_dir_buf.len);
    @memcpy(EditorState.active_browse_dir_buf[0..len], path[0..len]);
    EditorState.active_browse_dir_len = len;
}

/// The folder the asset browser is currently showing, or the project's
/// `assets/` root if the browser hasn't reported one yet. Empty if no project.
pub fn activeBrowseDir(buf: []u8) []const u8 {
    if (EditorState.active_browse_dir_len > 0) return EditorState.active_browse_dir_buf[0..EditorState.active_browse_dir_len];
    const proj = EditorState.project_path orelse return "";
    return std.fmt.bufPrint(buf, "{s}/assets", .{proj}) catch "";
}

// ── Reveal-in-browser request ────────────────────────────────────────────────

pub fn revealAsset(path: []const u8) void {
    if (path.len == 0) return;
    selectAsset(path);
    const len = @min(path.len, EditorState.reveal_asset_buf.len);
    @memcpy(EditorState.reveal_asset_buf[0..len], path[0..len]);
    EditorState.reveal_asset_len = len;
    EditorState.reveal_asset_request = EditorState.reveal_asset_buf[0..len];
}

pub fn takeRevealRequest() ?[]const u8 {
    const r = EditorState.reveal_asset_request;
    EditorState.reveal_asset_request = null;
    return r;
}
