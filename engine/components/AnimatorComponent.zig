const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Controls animation playback on the object.
pub const AnimatorComponent = struct {
    pub const is_component = true;

    /// Animation playback speed multiplier.
    speed: f32 = 1.0,

    pub const turian_hints = struct {
        pub const speed = FieldHint{ .min = 0.0, .max = 5.0, .widget = .slider_entry };
    };
};
