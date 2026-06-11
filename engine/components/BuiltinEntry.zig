/// Describes a builtin component in the BUILTIN_COMPONENTS registry.
pub const BuiltinEntry = struct {
    /// Zig type name (e.g. "CameraComponent").
    type_name: []const u8,
    /// Human-readable name (e.g. "Camera").
    display_name: []const u8,
};
