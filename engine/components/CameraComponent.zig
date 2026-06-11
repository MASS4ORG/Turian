const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Perspective or orthographic camera.
pub const CameraComponent = struct {
    pub const is_component = true;

    /// Field of view in degrees.
    fov: f32 = 60.0,
    /// Near clipping plane distance.
    near: f32 = 0.01,
    /// Far clipping plane distance.
    far: f32 = 1000.0,
    /// When true, use orthographic projection instead of perspective.
    orthographic: bool = false,

    pub const turian_hints = struct {
        pub const fov = FieldHint{ .min = 1.0, .max = 179.0, .widget = .slider };
        pub const near = FieldHint{ .min = 0.0001, .max = 1.0 };
        pub const far = FieldHint{ .min = 1.0, .max = 10000.0 };
    };
};
