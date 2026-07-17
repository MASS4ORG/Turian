const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Renders a mesh at the object's transform. A mesh with N submeshes (see
/// `engine.assets.Mesh.submeshes`) draws `materials[i]` for submesh `i`; a
/// mesh with no submesh table (procedural primitives, OBJ) draws its whole
/// index buffer with `materials[0]`. An unset or out-of-range slot falls
/// back to the default material.
pub const MeshRendererComponent = struct {
    pub const is_component = true;

    /// Max submesh materials a single mesh renderer can bind. Sized for
    /// complex imported models (e.g. Sponza has ~25 materials); primitives
    /// and simple models typically use just one slot.
    pub const MAX_SUBMESH_MATERIALS = 32;

    /// Whether this mesh casts shadows.
    cast_shadows: bool = true,
    /// Whether this mesh receives shadows.
    receive_shadows: bool = true,
    /// Reference to the mesh asset.
    mesh: TypedAssetRef(.mesh) = .{},
    /// Per-submesh material references, positionally bound to `mesh`'s
    /// submesh table. Only the first `material_count` entries are used.
    materials: [MAX_SUBMESH_MATERIALS]TypedAssetRef(.material) = .{TypedAssetRef(.material){}} ** MAX_SUBMESH_MATERIALS,
    /// Number of valid entries in `materials`.
    material_count: u32 = 0,

    pub const turian_hints = struct {
        // Hand-drawn (bounded by material_count) in the Inspector's
        // mesh_renderer branch instead of the generic fixed-array walker,
        // which would otherwise render all MAX_SUBMESH_MATERIALS slots.
        pub const materials = FieldHint{ .hidden = true };
    };
};
