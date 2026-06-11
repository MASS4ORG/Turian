const Guid = @import("guid").Guid;
const AssetType = @import("AssetType.zig").AssetType;
const ImportSettings = @import("ImportSettings.zig").ImportSettings;

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
};
