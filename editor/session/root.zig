//! Live editing session: open project/scene, selection, undo, clipboard, and mutation operations. GUI-free — the host supplies `io`, arenas, and callbacks. `EditorState` is the unified facade; prefer it over reaching into submodules directly.

/// Unified session state facade — runtime variables plus re-exported helpers.
pub const EditorState = @import("EditorState.zig");
/// Settings/asset-db/task-manager lifecycle and scene open-state accessors.
pub const state = @import("State.zig");
/// Multi-select bitset over the live scene nodes.
pub const selection = @import("Selection.zig");
/// Grouped undo/redo command stack.
pub const undo_redo = @import("UndoRedo.zig");
/// In-place rename of scene objects and assets.
pub const rename = @import("RenameOps.zig");
/// Scene-node clipboard and drag payload tracking.
pub const clipboard = @import("ClipboardAndDrag.zig");
/// Scene-tree structural edits: add, delete, duplicate, reparent, reorder.
pub const scene_tree = @import("SceneTreeOps.zig");
/// Prefab instantiate/override/apply/revert against the live scene.
pub const prefab_ops = @import("PrefabOps.zig");
/// GUID <-> path resolution and component-definition refresh.
pub const asset_resolution = @import("AssetResolution.zig");
/// Remote-debug protocol mutations applied to the live scene.
pub const debug_mutations = @import("DebugMutations.zig");
/// Background asset import job (project open).
pub const import_job = @import("ImportJob.zig");
/// Background user-script reflection job.
pub const reflect_job = @import("ReflectJob.zig");
/// Project open/create and scene save/load against the live session.
pub const project = @import("ProjectSession.zig");

test {
    // Force every submodule to be analysed so their `test` blocks are
    // collected — see the identical block in `editor/root.zig`.
    @import("std").testing.refAllDecls(@This());
}
