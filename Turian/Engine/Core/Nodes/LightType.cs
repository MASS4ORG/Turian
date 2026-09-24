namespace Turian.Engine.Core;

/// <summary>
/// How a <see cref="LightComponent"/> emits.
/// </summary>
public enum LightType
{
    /// <summary>
    /// Emits from the node's position in every direction, falling off with distance squared.
    /// </summary>
    Point = 0,

    /// <summary>
    /// Emits along the node's forward axis with no falloff, as sunlight or moonlight does.
    /// Position is ignored; only the node's orientation matters.
    /// </summary>
    Directional = 1,
}
