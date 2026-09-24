namespace Turian.Editor.Core;

/// <summary>
/// The transport state of the Studio's in-editor play mode.
/// </summary>
public enum PlayState
{
    /// <summary>
    /// No play session is running. The Game panel keeps previewing the edited scene's primary
    /// camera, live and non-interactive — it only goes blank if the scene has no camera.
    /// </summary>
    Stopped,

    /// <summary>A play session is running and advancing every frame.</summary>
    Playing,

    /// <summary>A play session is running but frozen; only explicit frame-steps advance it.</summary>
    Paused
}
