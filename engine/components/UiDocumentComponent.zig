const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;

/// Instantiates a `.uidoc` UI document into the scene (D1: UI-Toolkit's
/// `UIDocument` analogue — the thin host for a `VisualTreeAsset`-shaped
/// asset). UI nodes are not `SceneNode`s and never touch `Transform.zig`;
/// this component only carries the document reference.
pub const UiDocumentComponent = struct {
    pub const is_component = true;

    /// Reference to the `.uidoc` asset to instantiate.
    document: TypedAssetRef(.ui_document) = .{},
};
