namespace Turian.Engine.Core;

/// <summary>Controls the longitudinal UV coordinates of a polyline ribbon.</summary>
public enum LineTextureMode
{
    /// <summary>Maps one texture repeat over the full polyline length.</summary>
    Stretch,

    /// <summary>Maps one texture repeat per unit of polyline length.</summary>
    Tile,
}
