/// Serialisable mesh renderer component (GUID-based asset references).
pub const SceneMeshRenderer = struct {
    cast_shadows: bool = true,
    receive_shadows: bool = true,
    /// Stable asset GUID string (UUID format). Empty means no mesh assigned.
    mesh_guid: []const u8 = "",
    /// Material asset GUID strings indexed by material slot (scene format v2;
    /// v1 stored them per-submesh and is auto-migrated on load). An empty string
    /// at slot i means the default material.
    material_guids: []const []const u8 = &.{},
    /// Deprecated single-material GUID, superseded by `material_guids`.
    /// Present only so scenes saved before per-submesh materials existed
    /// still load correctly (`SceneIo.sceneCompToEngine` migrates it into
    /// `materials[0]` in memory and logs a warning); the editor never writes
    /// this field. Slated for removal.
    material_guid: []const u8 = "",
};
