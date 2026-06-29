//! Runtime introspection layer: structured, JSON-serialisable views
//! of live engine state, plus queries and mutation. The single source of truth
//! consumed by the editor, the CLI, the Remote Debug Protocol, and the
//! MCP server.

const Inspector = @import("Inspector.zig");

/// Engine-wide runtime metrics struct (FPS, memory, draw calls, ECS counts).
pub const Metrics = @import("Metrics.zig").Metrics;

/// A read-only view of one loaded scene handed to the inspector by the host.
pub const SceneView = Inspector.SceneView;
/// A read-only view of one project asset handed to the inspector by the host.
pub const AssetView = Inspector.AssetView;
/// Everything the inspector can see at one instant (scenes + metrics).
pub const World = Inspector.World;
/// A typed value used when mutating a field by name.
pub const Value = Inspector.Value;

// ── Events ────────────────────────────────────────────────────────
/// Runtime event catalog clients can subscribe to over the debug protocol.
pub const Event = @import("Event.zig").Event;
/// Writes the event catalog as JSON (used by the AI-context export).
pub const writeEventCatalog = @import("Event.zig").writeCatalog;

// ── Serialisation (writer-based, compose into any std.json.Stringify) ─────────
pub const writeComponent = Inspector.writeComponent;
pub const writeTransform = Inspector.writeTransform;
pub const writeEntitySummary = Inspector.writeEntitySummary;
pub const writeEntityDetail = Inspector.writeEntityDetail;
pub const writeScene = Inspector.writeScene;
pub const writeSceneList = Inspector.writeSceneList;
pub const writeAsset = Inspector.writeAsset;
pub const writeAssetList = Inspector.writeAssetList;
pub const writeSnapshot = Inspector.writeSnapshot;
pub const writeSchema = Inspector.writeSchema;
pub const componentTypeName = Inspector.componentTypeName;

// ── Serialisation (heap-allocating convenience) ───────────────────────────────
pub const snapshotJsonAlloc = Inspector.snapshotJsonAlloc;
pub const schemaJsonAlloc = Inspector.schemaJsonAlloc;
pub const entityJsonAlloc = Inspector.entityJsonAlloc;

// ── Queries ───────────────────────────────────────────────────────────────────
pub const componentIndex = Inspector.componentIndex;
pub const hasComponent = Inspector.hasComponent;
pub const findByComponent = Inspector.findByComponent;
pub const findByName = Inspector.findByName;
pub const findNear = Inspector.findNear;
pub const activeCameras = Inspector.activeCameras;
pub const lights = Inspector.lights;

// ── Mutation ──────────────────────────────────────────────────────────────────
pub const setComponentField = Inspector.setComponentField;
pub const setTransformField = Inspector.setTransformField;
pub const spawnEntity = Inspector.spawnEntity;
pub const destroyEntity = Inspector.destroyEntity;

test {
    @import("std").testing.refAllDecls(@This());
}
