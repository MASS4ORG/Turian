const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;

/// Renders a mesh at the object's transform.
pub const MeshRendererComponent = struct {
    pub const is_component = true;

    /// Whether this mesh casts shadows.
    cast_shadows: bool = true,
    /// Whether this mesh receives shadows.
    receive_shadows: bool = true,
    /// Reference to the mesh asset.
    mesh: TypedAssetRef(.mesh) = .{},
    /// Reference to the material asset. Empty falls back to the default material.
    material: TypedAssetRef(.material) = .{},
};
