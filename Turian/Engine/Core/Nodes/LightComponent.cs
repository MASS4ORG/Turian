namespace Turian.Engine.Core;

/// <summary>
/// Reusable light behavior that can be attached to any <see cref="Node"/>.
/// </summary>
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Light")]
[TypeId("a3000001-0000-4000-8000-000000000006")]
public class LightComponent : Component
{
    /// <summary>
    /// Gets or sets how the light emits. A <see cref="LightType.Directional"/> light ignores the
    /// node's position and shines along its forward axis.
    /// </summary>
    public LightType Type { get; set; } = LightType.Point;

    /// <summary>
    /// Gets or sets the intensity of the light. A point light's contribution falls off with
    /// distance squared, so a scene lit from d units away needs roughly d² here; a directional
    /// light does not attenuate, so useful values are near 1.
    /// </summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>
    /// Gets or sets the color of the light as RGBA.
    /// </summary>
    public Vector4 Color { get; set; } = new(1f);

    /// <summary>
    /// Creates a configured point light component.
    /// </summary>
    /// <param name="intensity">The light intensity.</param>
    /// <param name="color">The light color.</param>
    /// <returns>A configured <see cref="LightComponent"/>.</returns>
    public static LightComponent CreatePointLight(float intensity, Vector4 color)
    {
        return new LightComponent
        {
            Type = LightType.Point,
            Intensity = intensity,
            Color = color
        };
    }

    /// <summary>
    /// Creates a configured directional light, for a sun or a moon. The direction comes from the
    /// node's orientation, so rotate the node to aim it.
    /// </summary>
    /// <param name="intensity">The light intensity. Directional light does not attenuate.</param>
    /// <param name="color">The light color.</param>
    /// <returns>A configured <see cref="LightComponent"/>.</returns>
    public static LightComponent CreateDirectionalLight(float intensity, Vector4 color)
    {
        return new LightComponent
        {
            Type = LightType.Directional,
            Intensity = intensity,
            Color = color
        };
    }
}
