const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Physics body with mass and gravity response.
pub const RigidBodyComponent = struct {
    pub const is_component = true;

    /// Mass in kilograms.
    mass: f32 = 1.0,
    /// Whether gravity affects this body.
    use_gravity: bool = true,
    /// When true, the body is moved via script rather than physics.
    is_kinematic: bool = false,

    pub const turian_hints = struct {
        pub const mass = FieldHint{ .min = 0.0, .max = 1000.0, .widget = .slider_entry };
    };
};
