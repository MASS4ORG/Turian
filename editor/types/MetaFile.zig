const Guid = @import("guid").Guid;
const AssetType = @import("AssetType.zig").AssetType;
const ImportSettings = @import("ImportSettings.zig").ImportSettings;

/// One asset generated from a source during import (e.g. a material or texture
/// extracted from a glTF/GLB). A single source asset can produce many of these
/// — the "one-to-many" import case. Sub-assets live only in the cache
/// (`.cache/assets/{guid}{ext}`); they have no source file of their own.
pub const SubAsset = struct {
    /// Stable identity of the generated asset. Persisted so it survives
    /// reimport and stays referenceable (e.g. swapped in a scene).
    guid: Guid = Guid.nil(),
    /// Runtime category of the generated asset (.material, .image, ...).
    asset_type: AssetType = .unknown,
    /// Stable key identifying this sub-asset within its source, e.g.
    /// "material:0" or "image:2". Used to reuse the same GUID across reimports.
    key: []const u8 = "",
    /// Short display name carrying the runtime extension (e.g.
    /// "BottleMat.material"). Never an absolute path.
    name: []const u8 = "",
};

/// Persistent metadata file stored alongside every asset as `<path>.meta`.
/// Serialized to / from JSON via serde.zig.
pub const MetaFile = struct {
    /// Stable asset identity — survives renames and moves.
    /// Serialized as a UUID string "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx".
    guid: Guid = Guid.nil(),

    /// Broad asset category, derived from file extension on creation.
    asset_type: AssetType = .unknown,

    /// Incremented when the importer logic changes; triggers a forced reimport.
    importer_version: u32 = 0,

    /// FNV-1a hash of the source file at last import; used for change detection.
    source_hash: u64 = 0,

    /// Asset-type-specific import configuration.
    import_settings: ImportSettings = .{ .unknown = {} },

    /// GUIDs of other assets this asset depends on at source level.
    source_deps: []const Guid = &.{},

    /// GUIDs of imported artifact assets produced from this asset.
    artifact_deps: []const Guid = &.{},

    /// Assets generated from this source during import (the one-to-many case:
    /// materials and textures extracted from a model). Empty for simple assets.
    sub_assets: []const SubAsset = &.{},
};
