const Vector3 = @import("../root.zig").Vector3;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Region a local (non-global) volume affects.
pub const VolumeShape = enum { box, sphere };

pub const VignetteSettings = struct {
    /// When false, this volume contributes nothing to vignette blending.
    enabled: bool = false,
    /// Vignette darkening strength (0 = off).
    intensity: f32 = 0,
    /// Vignette outer radius as a fraction of half-diagonal distance from center.
    radius: f32 = 0.75,
    /// Vignette falloff softness (0 = hard edge).
    smoothness: f32 = 0.35,

    pub const turian_hints = struct {
        pub const intensity = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
        pub const radius = FieldHint{ .min = 0.0, .max = 1.5, .widget = .slider };
        pub const smoothness = FieldHint{ .min = 0.0, .max = 1.0, .widget = .slider };
    };
};

/// Lift/Gamma/Gain color grading (ASC CDL slope/offset/power form).
/// Each is a 3-vector (X=red, Y=green, Z=blue).
pub const ColorGradingSettings = struct {
    /// When false, this volume contributes nothing to grading blending.
    enabled: bool = false,
    /// Shadow lift (additive, 0 = no shift).
    lift: Vector3 = .{},
    /// Midtone gamma (1 = no shift).
    gamma: Vector3 = .{ .x = 1, .y = 1, .z = 1 },
    /// Highlight gain (multiplicative, 1 = no shift).
    gain: Vector3 = .{ .x = 1, .y = 1, .z = 1 },
};

pub const BloomSettings = struct {
    /// When false, this volume contributes nothing to bloom blending.
    enabled: bool = false,
    /// Brightness threshold in linear HDR (pixels above this glow).
    threshold: f32 = 1.0,
    /// Glow strength (0 = off).
    intensity: f32 = 0,
    /// Blur spread multiplier.
    radius: f32 = 1.0,

    pub const turian_hints = struct {
        pub const threshold = FieldHint{ .min = 0.0, .max = 10.0, .widget = .slider_entry };
        pub const intensity = FieldHint{ .min = 0.0, .max = 5.0, .widget = .slider };
        pub const radius = FieldHint{ .min = 0.0, .max = 4.0, .widget = .slider };
    };
};

/// Post-process volume: global (affects the whole scene at full weight) or
/// local (a box/sphere region around this node's `Transform`, blended by
/// distance). Overlapping volumes are blended by priority and distance-based
/// weight. Each effect category has its own `enabled` flag.
pub const PostProcessVolumeComponent = struct {
    pub const is_component = true;

    /// When true, this volume affects the whole scene at weight 1 regardless
    /// of camera position (`shape`/`extents`/`radius`/`blend_distance` are ignored).
    is_global: bool = true,
    shape: VolumeShape = .box,
    /// Box half-size along this node's local (rotated) axes. Ignored if `shape != .box`.
    extents: Vector3 = .{ .x = 5, .y = 5, .z = 5 },
    /// Sphere radius, centered on `Transform.position`. Ignored if `shape != .sphere`.
    radius: f32 = 5.0,
    /// Distance (world units) outside the shape over which weight falls off
    /// linearly from 1 to 0. 0 = hard edge.
    blend_distance: f32 = 2.0,
    /// Higher priority is blended later (wins where volumes overlap at equal weight).
    priority: f32 = 0,

    vignette: VignetteSettings = .{},
    grading: ColorGradingSettings = .{},
    bloom: BloomSettings = .{},

    pub const turian_hints = struct {
        pub const extents = FieldHint{ .group = "Shape" };
        pub const radius = FieldHint{ .min = 0.01, .max = 1000.0, .group = "Shape" };
        pub const blend_distance = FieldHint{ .min = 0.0, .max = 100.0, .group = "Shape" };
        pub const priority = FieldHint{ .min = -100.0, .max = 100.0, .group = "Shape" };
    };
};
