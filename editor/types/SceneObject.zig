const engine = @import("engine");
const SceneComponent = @import("SceneComponent.zig").SceneComponent;

/// Serialisable scene object for JSON persistence.
pub const SceneObject = struct {
    /// Object name.
    name: []const u8 = "",
    /// Stable GUID string. Empty in pre-GUID scene files (assigned on load).
    guid: []const u8 = "",
    /// Parent index, or -1 for root.
    parent: i32 = -1,
    /// Whether the object is active.
    active: bool = true,
    /// Local transform.
    transform: engine.Transform = .{},
    /// Component list.
    components: []const SceneComponent = &.{},

    // ── Prefab linkage (issue #32) ──────────────────────────────────────────
    /// Source prefab asset GUID — present only on a prefab-instance root.
    prefab_source: []const u8 = "",
    /// Corresponding template-node GUID inside the prefab — present on every
    /// node of a prefab instance. Empty for plain scene objects.
    prefab_node: []const u8 = "",
    /// Overridden prefab group keys ("name", "active", "transform", "components").
    overrides: []const []const u8 = &.{},
};
