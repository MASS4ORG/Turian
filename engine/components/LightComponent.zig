const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Light source in the scene.
pub const LightComponent = struct {
    pub const is_component = true;

    /// Light type.
    pub const Kind = enum { directional, point, spot };
    /// Light type.
    kind: Kind = .directional,
    /// Red channel of light colour (0..1).
    color_r: f32 = 1.0,
    /// Green channel of light colour (0..1).
    color_g: f32 = 1.0,
    /// Blue channel of light colour (0..1).
    color_b: f32 = 1.0,
    /// Light intensity multiplier.
    intensity: f32 = 1.0,
    /// Light range (for point/spot lights). Falloff reaches zero at this distance.
    range: f32 = 10.0,
    /// Spot cone outer half-angle in degrees (spot lights only). Light fades to
    /// zero at this angle from the spot direction.
    spot_angle: f32 = 35.0,
    /// Spot edge softness (0 = hard edge, 1 = fully soft). Controls the inner
    /// cone where the light is at full strength relative to `spot_angle`.
    spot_softness: f32 = 0.15,
    /// Whether this light casts shadows. Only the first shadow-casting
    /// directional light is used for the viewport shadow map.
    cast_shadows: bool = true,

    pub const turian_hints = struct {
        pub const color_r = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
        pub const color_g = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
        pub const color_b = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
        // Windowed inverse-square falloff means a light needs an intensity in
        // the hundreds/thousands to read as bright at real-world "meters"-scale
        // distances (tens of units) — a max of 10 silently clamped every
        // attempt to compensate for a large scene (e.g. Bistro, ~170 units
        // across) by cranking intensity up via the slider-entry widget.
        pub const intensity = FieldHint{ .min = 0.0, .max = 5000.0, .widget = .slider_entry };
        pub const range = FieldHint{ .min = 0.0, .max = 2000.0, .widget = .slider_entry };
        pub const spot_angle = FieldHint{ .min = 1.0, .max = 89.0, .widget = .slider_entry };
        pub const spot_softness = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
    };
};
