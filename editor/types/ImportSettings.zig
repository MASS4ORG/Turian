const engine = @import("engine");
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
    /// DDS only: invert the green (Y) channel of BC5-compressed normal maps,
    /// for sources authored in DirectX's normal-map convention.
    flip_green_channel: bool = false,
};

/// Settings for audio asset import.
pub const AudioImportSettings = struct {
    streaming: bool = false,
    normalize: bool = false,
};

/// A hand-authored correction merged over one material of an imported model at
/// cook time, keyed by the source material's own name (stable across reimport,
/// unlike sub-asset GUIDs which are per-project). Every field is optional and
/// only replaces the derived value when set — this is a patch, not a full
/// material description. Exists because some DCC formats carry no signal at
/// all for certain properties (e.g. plain FBX glass has no opacity anywhere),
/// so the importer's best-effort derivation has nothing to derive from.
pub const MaterialOverride = struct {
    material_name: []const u8 = "",
    base_color: ?[4]f32 = null,
    metallic: ?f32 = null,
    roughness: ?f32 = null,
    emissive: ?[3]f32 = null,
    emissive_strength: ?f32 = null,
    normal_scale: ?f32 = null,
    occlusion_strength: ?f32 = null,
    alpha_mode: ?engine.assets.AlphaMode = null,
    alpha_cutoff: ?f32 = null,
    double_sided: ?bool = null,
};

/// Settings for model asset import.
pub const ModelImportSettings = struct {
    import_animations: bool = true,
    import_materials: bool = true,
    scale_factor: f32 = 1.0,
    material_overrides: []const MaterialOverride = &.{},
};

/// Settings for font asset import (v1: theme fonts only; this
/// governs the point size a font registers at when a future UI text
/// component gains a typed font reference).
pub const FontImportSettings = struct {
    default_size: f32 = 16,
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
    font: FontImportSettings,
    ui_theme: void,
    // Not a project asset — never imported/cached.
    studio_settings: void,
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
        .font => .{ .font = .{} },
        .ui_theme => .{ .ui_theme = {} },
        .studio_settings => .{ .studio_settings = {} },
        .unknown => .{ .unknown = {} },
    };
}
