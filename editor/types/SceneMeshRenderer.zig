/// Serialisable mesh renderer component (GUID-based asset references).
pub const SceneMeshRenderer = struct {
    cast_shadows: bool = true,
    receive_shadows: bool = true,
    /// Stable asset GUID string (UUID format). Empty means no mesh assigned.
    mesh_guid: []const u8 = "",
    /// Per-submesh material asset GUID strings, positionally bound to the
    /// mesh's submesh table. An empty string at index i means the default
    /// material.
    material_guids: []const []const u8 = &.{},
    /// Deprecated single-material GUID, superseded by `material_guids`.
    /// Present only so scenes saved before per-submesh materials existed
    /// still load correctly (`SceneIo.sceneCompToEngine` migrates it into
    /// `materials[0]` in memory and logs a warning); the editor never writes
    /// this field. Slated for removal.
    material_guid: []const u8 = "",
};
