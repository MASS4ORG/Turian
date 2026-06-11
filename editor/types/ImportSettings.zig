const AssetType = @import("AssetType.zig").AssetType;

/// Texture filtering mode.
pub const ImageFilter = enum { linear, nearest };
/// Texture wrap mode.
pub const ImageWrap = enum { repeat, clamp };

/// Settings for image asset import.
pub const ImageImportSettings = struct {
    generate_mipmaps: bool = true,
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
        .unknown => .{ .unknown = {} },
    };
}
