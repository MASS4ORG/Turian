const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Renders a mesh at the object's transform. Each of the mesh's submeshes (see
/// `engine.assets.Mesh.Submesh`) draws `materials[submesh.material_slot]` — the
/// table is keyed by material slot, not submesh index, so a mesh with many
/// submeshes sharing few materials stays compact. A mesh with no submesh table
/// (procedural primitives, OBJ) draws its whole index buffer with `materials[0]`.
/// An unset or out-of-range slot falls back to the default material.
pub const MeshRendererComponent = struct {
    pub const is_component = true;

    /// Max distinct materials a single mesh renderer can bind, one per material
    /// slot. Sized for complex imported models flattened into one mesh (e.g. the
    /// Bistro exterior has 132 slots); primitives and simple models use one slot.
    /// The component stays a plain POD array so the play-mode C-ABI exchange
    /// needs no side table. This ceiling stays at or below the `Component`
    /// union's largest variant, so it does not grow `SceneNode`.
    pub const MAX_MATERIALS = 160;

    /// Whether this mesh casts shadows.
    cast_shadows: bool = true,
    /// Whether this mesh receives shadows.
    receive_shadows: bool = true,
    /// Reference to the mesh asset.
    mesh: TypedAssetRef(.mesh) = .{},
    /// Material references indexed by `Submesh.material_slot`. Only the first
    /// `material_count` entries (the mesh's unique material slots) are used.
    materials: [MAX_MATERIALS]TypedAssetRef(.material) = .{TypedAssetRef(.material){}} ** MAX_MATERIALS,
    /// Number of valid material slots in `materials`.
    material_count: u32 = 0,

    pub const turian_hints = struct {
        // Hand-drawn (bounded by material_count) in the Inspector's
        // mesh_renderer branch instead of the generic fixed-array walker,
        // which would otherwise render all MAX_MATERIALS slots.
        pub const materials = FieldHint{ .hidden = true };
    };
};
