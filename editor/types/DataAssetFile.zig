const SceneScriptField = @import("SceneScriptField.zig").SceneScriptField;

/// Serialisable data-asset instance for JSON persistence.
/// GUID is NOT stored here — it lives in the sibling `.asset.meta` file.
pub const DataAssetFile = struct {
    /// Schema version; bump to trigger migration logic.
    version: u32 = 1,
    /// Zig type name of the data-asset struct (e.g. "EnemyStats").
    type_name: []const u8 = "",
    /// Source file path where the type is defined.
    source_file: []const u8 = "",
    /// Field values in definition order.
    fields: []const SceneScriptField = &.{},
};
