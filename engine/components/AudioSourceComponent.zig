const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Audio playback source attached to a game object.
pub const AudioSourceComponent = struct {
    pub const is_component = true;

    /// Playback volume (0..1).
    volume: f32 = 1.0,
    /// Playback pitch multiplier.
    pitch: f32 = 1.0,
    /// Whether the audio loops when finished.
    loop: bool = false,
    /// Whether playback starts automatically on scene load.
    play_on_awake: bool = true,

    pub const turian_hints = struct {
        pub const volume = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
        pub const pitch = FieldHint{ .min = 0.1, .max = 3.0, .widget = .slider };
    };
};
