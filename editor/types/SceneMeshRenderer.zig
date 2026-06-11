/// Serialisable mesh renderer component (GUID-based asset references).
pub const SceneMeshRenderer = struct {
    cast_shadows: bool = true,
    receive_shadows: bool = true,
    /// Stable asset GUID string (UUID format). Empty means no mesh assigned.
    mesh_guid: []const u8 = "",
    /// Material asset GUID string. Empty means the default material.
    material_guid: []const u8 = "",
};
