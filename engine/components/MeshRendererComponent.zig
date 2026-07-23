const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Renders a mesh at the object's transform. Material bindings are keyed by slot, not submesh index.
pub const MeshRendererComponent = struct {
    pub const is_component = true;

    /// Max distinct materials per mesh renderer, one per material slot.
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
        // Hand-drawn in the Inspector instead of the generic fixed-array walker.
        pub const materials = FieldHint{ .hidden = true };
    };
};
