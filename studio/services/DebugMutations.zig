const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");

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

// ── Remote-debug mutation helpers ────────────────────────────
//
// The Remote Debug Protocol's MutationApplier (see studio/Main.zig) routes LLM /
// CLI edits through these, so AI edits go through the same undo stack as the UI
// and stay consistent with it.

const introspect = engine.introspect;

/// Live runtime metrics exposed to the Remote Debug Protocol (`metrics` method).
/// Refreshed each frame by `refreshDebugMetrics` before the debug server pumps.
/// Recomputes `debug_metrics` from the engine profiler (render counters + frame
/// timing) plus live scene counts. Cheap; safe to call once per frame.
pub fn refreshDebugMetrics() void {
    EditorState.debug_metrics = introspect.Metrics.fromProfiler(engine.Profiler.captured());
    var comps: u32 = 0;
    for (0..EditorState.object_count) |i| comps += @intCast(EditorState.objects[i].component_count);
    EditorState.debug_metrics.withScene(if (EditorState.scene_open) 1 else 0, @intCast(EditorState.object_count), comps);
}

/// Maximum assets exposed to the debug protocol's `asset.list` in one snapshot.
const MAX_DEBUG_ASSETS = 1024;
var debug_asset_views: [MAX_DEBUG_ASSETS]introspect.AssetView = undefined;
var debug_asset_guid_bufs: [MAX_DEBUG_ASSETS][36]u8 = undefined;

/// Rebuilds the asset view list from the live `AssetDatabase` and returns a
/// borrowed slice valid until the next call (single-threaded; refreshed each
/// frame before the debug server pumps).
pub fn refreshDebugAssets() []const introspect.AssetView {
    if (!State.assetDbReady()) return &.{};
    var n: usize = 0;
    var it = EditorState.asset_db.by_guid.valueIterator();
    while (it.next()) |info| {
        if (n >= MAX_DEBUG_ASSETS) break;
        debug_asset_views[n] = .{
            .guid = info.guid.toString(&debug_asset_guid_bufs[n]),
            .path = info.path,
            .type = @tagName(info.asset_type),
        };
        n += 1;
    }
    return debug_asset_views[0..n];
}

/// Reloads an asset by GUID for the remote-debug `asset.reload` mutation.
/// Returns false if the GUID is unknown. Triggers a component/asset refresh so
/// the change is picked up by the editor (and the viewport) on the next frame.
pub fn debugReloadAsset(io: std.Io, arena: std.mem.Allocator, guid_str: []const u8) bool {
    if (!State.assetDbReady()) return false;
    const guid = editor.Guid.parse(guid_str) catch return false;
    if (!EditorState.asset_db.by_guid.contains(guid)) return false;
    const AssetResolution = @import("AssetResolution.zig");
    AssetResolution.refreshComponents(io, arena);
    return true;
}

/// First object whose name exactly matches `name`, or null.
pub fn findObjectByName(name: []const u8) ?usize {
    for (0..EditorState.object_count) |i| {
        if (std.mem.eql(u8, EditorState.objects[i].nameSlice(), name)) return i;
    }
    return null;
}

/// Sets a single component field by name on `idx`, recording an undoable edit.
/// Returns false if the object/component/field is unknown or the value mismatches.
pub fn debugSetComponentField(now: i128, idx: usize, component: []const u8, field: []const u8, value: introspect.Value) bool {
    if (idx >= EditorState.object_count) return false;
    const ci = introspect.componentIndex(&EditorState.objects[idx], component) orelse return false;
    const before = EditorState.objects[idx];
    if (!introspect.setComponentField(&EditorState.objects[idx].components[ci], field, value)) return false;
    const after = EditorState.objects[idx];
    UndoRedo.pushCommand(now, &.{ .modify_object = .{ .idx = idx, .before = before, .after = after } });
    EditorState.scene_dirty = true;
    return true;
}

/// Sets a transform channel (position/rotation/scale) on `idx`, undoable.
pub fn debugSetTransform(now: i128, idx: usize, channel: []const u8, value: [3]f32) bool {
    if (idx >= EditorState.object_count) return false;
    const before = EditorState.objects[idx];
    if (!introspect.setTransformField(&EditorState.objects[idx], channel, value)) return false;
    const after = EditorState.objects[idx];
    UndoRedo.pushCommand(now, &.{ .modify_object = .{ .idx = idx, .before = before, .after = after } });
    EditorState.scene_dirty = true;
    return true;
}
