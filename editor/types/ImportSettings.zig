const AssetType = @import("AssetType.zig").AssetType;

/// Texture filtering mode.
pub const ImageFilter = enum { linear, nearest };
/// Texture wrap mode.
pub const ImageWrap = enum { repeat, clamp };

/// How an imported image is intended to be used. Drives sensible defaults for
/// color space and compression.
pub const TextureType = enum { default, normal_map, sprite, ui, hdr };

/// Color space the source image is authored in. Albedo/UI are sRGB; data maps
/// (normal/roughness/metallic) are linear.
pub const ColorSpace = enum { srgb, linear };

/// GPU compression applied on import. `auto` picks a BCn format from the
/// texture type; `none` keeps RGBA8.
pub const TextureCompression = enum { none, auto, bc7, bc3, bc1 };

/// Settings for image asset import.
pub const ImageImportSettings = struct {
    texture_type: TextureType = .default,
    color_space: ColorSpace = .srgb,
    generate_mipmaps: bool = true,
    compression: TextureCompression = .none,
    filter: ImageFilter = .linear,
    wrap: ImageWrap = .repeat,
    max_size: u32 = 2048,
};

/// Settings for audio asset import.
pub const AudioImportSettings = struct {
    streaming: bool = false,
    normalize: bool = false,
};

/// Settings for model asset import.
pub const ModelImportSettings = struct {
    import_animations: bool = true,
    import_materials: bool = true,
    scale_factor: f32 = 1.0,
};

/// Per-asset-type import configuration.
/// Serializes as an externally-tagged JSON object, e.g.:
///   {"image": {"generate_mipmaps": true, "filter": "linear", ...}}
pub const ImportSettings = union(AssetType) {
    unknown: void,
    script: void,
    image: ImageImportSettings,
    audio: AudioImportSettings,
    model: ModelImportSettings,
    scene: void,
    material: void,
    data_asset: void,
    input_actions: void,
    project_settings: void,
    ui_document: void,
};

/// Returns default import settings for the given asset type.
pub fn defaultFor(asset_type: AssetType) ImportSettings {
    return switch (asset_type) {
        .image => .{ .image = .{} },
        .audio => .{ .audio = .{} },
        .model => .{ .model = .{} },
        .script => .{ .script = {} },
        .scene => .{ .scene = {} },
        .material => .{ .material = {} },
        .data_asset => .{ .data_asset = {} },
        .input_actions => .{ .input_actions = {} },
        .project_settings => .{ .project_settings = {} },
        .ui_document => .{ .ui_document = {} },
        .unknown => .{ .unknown = {} },
    };
}
