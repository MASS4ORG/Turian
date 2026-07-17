/// Format-agnostic materials/images extracted from a source model (glTF/FBX),
/// shared between loaders so `AssetImporter.generateModelDerived` can turn any
/// of them into `.material`/`.image` sub-assets the same way.
const std = @import("std");

/// Rendering alpha mode (matches the metallic-roughness model).
pub const AlphaMode = enum { @"opaque", mask, blend };

/// A reference from a material slot to one of the model's images.
pub const TexRef = struct {
    /// Index into `ModelInfo.images`, or null when this slot binds no texture.
    image_index: ?u32 = null,
    /// Texcoord set the slot samples; usually 0.
    uv_set: u32 = 0,
};

/// A source material flattened to the metallic-roughness model. Strings/refs
/// are owned by the parent `ModelInfo`.
pub const MaterialInfo = struct {
    name: []const u8,
    base_color: [4]f32,
    metallic: f32,
    roughness: f32,
    emissive: [3]f32,
    emissive_strength: f32,
    normal_scale: f32,
    occlusion_strength: f32,
    alpha_mode: AlphaMode,
    alpha_cutoff: f32,
    double_sided: bool,
    albedo: TexRef,
    metallic_roughness: TexRef,
    normal: TexRef,
    /// Emissive texture (distinct from the `emissive` colour factor above).
    emissive_map: TexRef,
    occlusion: TexRef,
};

/// A source image: either an external file (`uri` non-empty) or embedded
/// bytes (`data` non-empty). Owned by the parent `ModelInfo`.
pub const ImageInfo = struct {
    name: []const u8,
    /// External relative path; empty when the image is embedded.
    uri: []const u8,
    /// MIME type (e.g. "image/png"); set for embedded images.
    mime_type: []const u8,
    /// Embedded image bytes; empty when the image is external.
    data: []const u8,

    /// True when the image is embedded (carries its own bytes).
    pub fn isEmbedded(self: ImageInfo) bool {
        return self.data.len > 0;
    }
};

/// A source model's materials and images. All strings and byte buffers are
/// owned by `arena`; call `deinit` to release them.
pub const ModelInfo = struct {
    materials: []MaterialInfo,
    images: []ImageInfo,
    arena: std.heap.ArenaAllocator,

    pub fn deinit(self: *ModelInfo) void {
        self.arena.deinit();
    }
};
