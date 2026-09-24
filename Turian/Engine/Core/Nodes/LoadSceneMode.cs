namespace Turian.Engine.Core;

/// <summary>
/// Specifies how a scene should be loaded.
/// </summary>
public enum LoadSceneMode
{
    /// <summary>
    /// Unloads all currently loaded scenes before loading the new scene.
    /// </summary>
    Single,

    /// <summary>
    /// Adds the new scene to the currently loaded scenes.
    /// </summary>
    Additive
}
