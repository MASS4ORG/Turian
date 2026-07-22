const TypedAssetRef = @import("../api/AssetRef.zig").TypedAssetRef;
const FieldHint = @import("../core/FieldHint.zig").FieldHint;

/// Image-based environment lighting for the scene: a skybox plus diffuse/specular
/// ambient contribution sampled from an equirectangular HDR map. At most one
/// active instance is used per scene (the first one found).
pub const EnvironmentComponent = struct {
    pub const is_component = true;

    /// Equirectangular HDR environment map (`.hdr`).
    env_map: TypedAssetRef(.texture) = .{},
    /// Multiplier applied to the environment's diffuse/specular/skybox contribution.
    intensity: f32 = 1.0,
    /// Draw the environment as the background. When false, diffuse/specular
    /// IBL still applies but the viewport background falls back to the
    /// renderer's plain clear color — useful while iterating on scene
    /// geometry/materials without a busy HDRI competing for attention.
    show_skybox: bool = true,

    pub const turian_hints = struct {
        pub const intensity = FieldHint{ .min = 0.0, .max = 10.0, .widget = .slider_entry };
    };
};
