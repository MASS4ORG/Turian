const FieldType = @import("FieldType.zig").FieldType;
const FieldValue = @import("FieldValue.zig").FieldValue;

/// C-ABI description of a single component field for user script reflection.
pub const FieldInfo = extern struct {
    /// Null-terminated field name.
    name: [*:0]const u8,
    /// The field's data type.
    field_type: FieldType,
    /// Default value for the field.
    default_value: FieldValue,
};
