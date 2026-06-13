const FieldType = @import("FieldType.zig").FieldType;
const FieldValue = @import("FieldValue.zig").FieldValue;
const AssetFilter = @import("AssetRef.zig").AssetFilter;

/// C-ABI description of a single component field for user script reflection.
pub const FieldInfo = extern struct {
    /// Null-terminated field name.
    name: [*:0]const u8,
    /// The field's data type.
    field_type: FieldType,
    /// Default value for the field.
    default_value: FieldValue,
    /// Asset category for `asset_ref` fields (drives the inspector's typed
    /// asset picker). `.any` for non-asset fields and unfiltered refs.
    asset_filter: AssetFilter = .any,
};
