const Vector3 = @import("../root.zig").Vector3;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;
const VolumeShape = @import("PostProcessVolumeComponent.zig").VolumeShape;

/// A baked local cubemap for specular reflections, filling the gap SSR can't:
/// off-screen, occluded, or interior surfaces that never appear in the
/// screen-space history buffer. Static — captured once (on scene load, or
/// on-demand when the probe's transform/capture settings change), never
/// re-baked per frame or in Play mode. Overlapping probes are resolved
/// per-shading-point on the CPU using the same box/sphere/blend_distance/
/// priority model as `PostProcessVolumeComponent`.
pub const ReflectionProbeComponent = struct {
    pub const is_component = true;

    /// A global probe is always a weight-1 candidate everywhere (no shape
    /// test) — a scene-wide fallback that still loses to any local probe
    /// with nonzero weight at the same point.
    is_global: bool = false,
    shape: VolumeShape = .box,
    /// Box half-size along this node's local (rotated) axes. Ignored if `shape != .box`.
    extents: Vector3 = .{ .x = 5, .y = 5, .z = 5 },
    /// Sphere radius, centered on `Transform.position`. Ignored if `shape != .sphere`.
    radius: f32 = 5.0,
    /// Distance (world units) outside the shape over which weight falls off
    /// linearly from 1 to 0. 0 = hard edge.
    blend_distance: f32 = 1.0,
    /// Higher priority wins where probes overlap at equal weight.
    priority: f32 = 0,

    /// Cube-face render resolution for the capture pass itself, before GGX
    /// prefiltering. Higher = sharper near-mirror reflections, more bake
    /// cost/VRAM.
    capture_size: u32 = 256,
    /// Capture camera near/far clip planes, world units.
    near: f32 = 0.05,
    far: f32 = 100.0,
    /// Multiplies the probe's contribution, mirroring `EnvironmentComponent.intensity`.
    intensity: f32 = 1.0,

    pub const turian_hints = struct {
        pub const extents = FieldHint{ .group = "Shape" };
        pub const radius = FieldHint{ .min = 0.01, .max = 1000.0, .group = "Shape" };
        pub const blend_distance = FieldHint{ .min = 0.0, .max = 100.0, .group = "Shape" };
        pub const priority = FieldHint{ .min = -100.0, .max = 100.0, .group = "Shape" };
        pub const capture_size = FieldHint{ .min = 32.0, .max = 1024.0, .group = "Capture" };
        pub const near = FieldHint{ .min = 0.001, .max = 100.0, .group = "Capture" };
        pub const far = FieldHint{ .min = 0.1, .max = 10000.0, .group = "Capture" };
        pub const intensity = FieldHint{ .min = 0.0, .max = 10.0, .widget = .slider, .group = "Capture" };
    };
};
