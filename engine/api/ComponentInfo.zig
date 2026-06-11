const FieldInfo = @import("FieldInfo.zig").FieldInfo;

/// C-ABI description of a component type for user script reflection.
/// Passed across the dynamic-library boundary via getRegistry().
pub const ComponentInfo = extern struct {
    /// Null-terminated fully qualified type name.
    name: [*:0]const u8,
    /// Pointer to the field array.
    fields: [*]const FieldInfo,
    /// Number of reflected fields.
    field_count: usize,
};
