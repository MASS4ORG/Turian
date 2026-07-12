//  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  EditorState: Unified public interface for split state modules
//  Declares runtime state variables and re-exports helper functions
//  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const build_options = @import("turian_build_options");

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

// ── Module imports (re-export later) ──────────────────────────────────────

pub const UndoRedo = @import("UndoRedo.zig");
pub const Selection = @import("Selection.zig");
pub const RenameOps = @import("RenameOps.zig");
pub const ClipboardAndDrag = @import("ClipboardAndDrag.zig");
pub const SceneTreeOps = @import("SceneTreeOps.zig");
pub const DebugMutations = @import("DebugMutations.zig");
pub const AssetResolution = @import("AssetResolution.zig");
pub const ReflectJob = @import("ReflectJob.zig");
pub const PrefabOps = @import("PrefabOps.zig");
pub const State = @import("State.zig");

// ── Runtime state variables (publicly accessible) ──────────────────────────

pub var gpa: std.mem.Allocator = std.heap.page_allocator;
pub var environ_map: *const std.process.Environ.Map = undefined;
pub var settings: editor.Settings = undefined;
pub var asset_db: editor.AssetDatabase = undefined;
pub var asset_refresh_generation: u64 = 0;
pub var objects: [MAX_OBJECTS]SceneNode = undefined;
pub var object_count: usize = 0;
pub var selected_object: ?usize = null;
pub var project_path_buf: [1024]u8 = undefined;
pub var project_path: ?[]const u8 = null;
pub var current_project: ?Project = null;
pub var discovered_components: [MAX_DISCOVERED]ComponentDef = undefined;
pub var discovered_count: usize = 0;
pub var discovered_events: [editor.event_scanner.MAX_EVENTS]editor.event_scanner.EventDef = undefined;
pub var discovered_event_count: usize = 0;
pub var scene_dirty: bool = false;
pub var saved_undo_depth: ?usize = 0;
pub var current_scene_path_buf: [1024]u8 = undefined;
pub var current_scene_path: ?[]const u8 = null;
pub var selected_asset_path_buf: [1024]u8 = undefined;
pub var selected_asset_path_len: usize = 0;
pub var selected_asset_path: ?[]const u8 = null;
pub var reveal_asset_buf: [1024]u8 = undefined;
pub var reveal_asset_len: usize = 0;
pub var reveal_asset_request: ?[]const u8 = null;
pub var drag_kind: ClipboardAndDrag.DragKind = .none;
pub var drag_object_idx: usize = 0;
pub var drag_asset_path_buf: [512]u8 = undefined;
pub var drag_asset_path_len: usize = 0;
pub var scene_open: bool = false;
pub var debug_metrics: engine.introspect.Metrics = .{};
pub var selected_set: [MAX_OBJECTS]bool = .{false} ** MAX_OBJECTS;
pub var last_select_idx: ?usize = null;
pub var g_rename: RenameOps.RenameState = .{};
pub var undo_alloc: std.mem.Allocator = std.heap.page_allocator;
pub var undo_len: usize = 0;
pub var redo_len: usize = 0;
pub var reflect_generation: usize = 0;
pub var reflect_job: ?*ReflectJob.ReflectJob = null;
pub var reflect_future: std.Io.Future(void) = undefined;
pub var reflect_pending: ?*ReflectJob.ReflectJob = null;
pub var active_browse_dir_buf: [1024]u8 = undefined;
pub var active_browse_dir_len: usize = 0;
pub var asset_db_initialized: bool = false;

// ── Settings and initialization functions ──────────────────────────────────

pub const initSettings = State.initSettings;
pub const deinitSettings = State.deinitSettings;
pub const settingsReady = State.settingsReady;
pub const assetDbReady = State.assetDbReady;
pub const taskManager = State.taskManager;

// ── Undo/Redo functions ────────────────────────────────────────────────────

pub const MAX_UNDO = UndoRedo.MAX_UNDO;
pub const Snapshot = UndoRedo.Snapshot;
pub const UndoCommand = UndoRedo.UndoCommand;
pub const initUndo = UndoRedo.initUndo;
pub const beginGroup = UndoRedo.beginGroup;
pub const endGroup = UndoRedo.endGroup;
pub const pushCommand = UndoRedo.pushCommand;
pub const undo = UndoRedo.undo;
pub const redo = UndoRedo.redo;
pub const canUndo = UndoRedo.canUndo;
pub const canRedo = UndoRedo.canRedo;
pub const undoLabel = UndoRedo.undoLabel;
pub const redoLabel = UndoRedo.redoLabel;
pub const clearUndoStack = UndoRedo.clearUndoStack;

// ── Selection functions ────────────────────────────────────────────────────

pub const isObjectSelected = Selection.isObjectSelected;
pub const selectObject = Selection.selectObject;
pub const deselectObject = Selection.deselectObject;
pub const toggleSelectObject = Selection.toggleSelectObject;
pub const clearSelectedObjects = Selection.clearSelectedObjects;
pub const selectObjectRange = Selection.selectObjectRange;
pub const selectedCount = Selection.selectedCount;

// ── Rename operations ──────────────────────────────────────────────────────

pub const RenameTarget = RenameOps.RenameTarget;
pub const RenameState = RenameOps.RenameState;
pub const startRenameObject = RenameOps.startRenameObject;
pub const startRenameAsset = RenameOps.startRenameAsset;
pub const commitRename = RenameOps.commitRename;
pub const cancelRename = RenameOps.cancelRename;
pub const isRenaming = RenameOps.isRenaming;

// ── Clipboard and drag operations ──────────────────────────────────────────

pub const DragKind = ClipboardAndDrag.DragKind;
pub const dragAssetPath = ClipboardAndDrag.dragAssetPath;
pub const startDragObject = ClipboardAndDrag.startDragObject;
pub const startDragAsset = ClipboardAndDrag.startDragAsset;
pub const clearDrag = ClipboardAndDrag.clearDrag;
pub const endFrameDrag = ClipboardAndDrag.endFrameDrag;
pub const hasClipboard = ClipboardAndDrag.hasClipboard;
pub const copySelectedObjects = ClipboardAndDrag.copySelectedObjects;
pub const pasteObjects = ClipboardAndDrag.pasteObjects;

// ── Scene tree operations ──────────────────────────────────────────────────

pub const deleteSelectedObjects = SceneTreeOps.deleteSelectedObjects;
pub const duplicateSelectedObjects = SceneTreeOps.duplicateSelectedObjects;
pub const deleteObject = SceneTreeOps.deleteObject;
pub const duplicateObject = SceneTreeOps.duplicateObject;
pub const focusOnObject = SceneTreeOps.focusOnObject;
pub const moveObjectBefore = SceneTreeOps.moveObjectBefore;
pub const isAncestorOrSelf = SceneTreeOps.isAncestorOrSelf;
pub const reparentObject = SceneTreeOps.reparentObject;
pub const addObject = SceneTreeOps.addObject;
pub const addObjectWithUndo = SceneTreeOps.addObjectWithUndo;

// ── Debug mutations ────────────────────────────────────────────────────────

pub const refreshDebugMetrics = DebugMutations.refreshDebugMetrics;
pub const refreshDebugAssets = DebugMutations.refreshDebugAssets;
pub const debugReloadAsset = DebugMutations.debugReloadAsset;
pub const findObjectByName = DebugMutations.findObjectByName;
pub const debugSetComponentField = DebugMutations.debugSetComponentField;
pub const debugSetTransform = DebugMutations.debugSetTransform;

// ── Asset resolution ──────────────────────────────────────────────────────

pub const resolveAssetGuid = AssetResolution.resolveAssetGuid;
pub const firstScenePath = AssetResolution.firstScenePath;
pub const modelPrimaryMaterial = AssetResolution.modelPrimaryMaterial;
pub const resolveObjectGuid = AssetResolution.resolveObjectGuid;
pub const dragAssetGuidStr = AssetResolution.dragAssetGuidStr;
pub const setProjectPath = AssetResolution.setProjectPath;
pub const refreshComponents = AssetResolution.refreshComponents;
pub const makeComponent = AssetResolution.makeComponent;
pub const syncSceneWithDefinitions = AssetResolution.syncSceneWithDefinitions;

// ── Reflect job operations ────────────────────────────────────────────────

pub const launchReflect = ReflectJob.launchReflect;
pub const dispatchReflect = ReflectJob.dispatchReflect;
pub const runReflectJob = ReflectJob.runReflectJob;
pub const finishReflect = ReflectJob.finishReflect;
pub const pumpReflect = ReflectJob.pumpReflect;
pub const waitForReflect = ReflectJob.waitForReflect;

// ── Prefab operations ──────────────────────────────────────────────────────

pub const prefabInstanceRoot = PrefabOps.prefabInstanceRoot;
pub const createPrefabFromObject = PrefabOps.createPrefabFromObject;
pub const instantiatePrefab = PrefabOps.instantiatePrefab;
pub const recomputePrefabOverrides = PrefabOps.recomputePrefabOverrides;
pub const resyncPrefabInstances = PrefabOps.resyncPrefabInstances;
pub const revertPrefabInstance = PrefabOps.revertPrefabInstance;
pub const applyPrefabInstance = PrefabOps.applyPrefabInstance;

// ── State accessor functions (from State.zig) ──────────────────────────────

pub const markSceneSaved = State.markSceneSaved;
pub const hasOpenScene = State.hasOpenScene;
pub const setCurrentScenePath = State.setCurrentScenePath;
pub const clearScene = State.clearScene;
pub const initDefaultScene = State.initDefaultScene;
pub const clearSelectedAsset = State.clearSelectedAsset;
pub const activeBrowseDir = State.activeBrowseDir;
pub const setActiveBrowseDir = State.setActiveBrowseDir;
pub const selectAsset = State.selectAsset;
pub const revealAsset = State.revealAsset;
pub const takeRevealRequest = State.takeRevealRequest;
