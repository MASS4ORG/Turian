const SceneScriptField = @import("SceneScriptField.zig").SceneScriptField;

/// Serialisable user script component reference for JSON persistence.
pub const SceneUserScript = struct {
    /// Component type name.
    type_name: []const u8 = "",
    /// Source file path.
    source_file: []const u8 = "",
    /// Reflected field values.
    fields: []const SceneScriptField = &.{},
};
