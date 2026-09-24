namespace Gaya.Plugin.Turian;

/// <summary>
/// How the Scene view's free camera responds to input. Applied to the viewport's
/// <c>SceneCameraController</c> every frame, so an edit is visible on the next drag.
/// </summary>
[EditorSetting("Scene Viewer", Description = "How the Scene view camera flies, looks and zooms.")]
public sealed class EditorCameraSettings
{
    /// <summary>Metres per second the camera flies at, before the Shift multiplier.</summary>
    [EditorSetting("Move Speed", Description = "Metres per second with WASD. Holding Shift is four times this.")]
    [Range(0.1f, 100f)]
    public float MoveSpeed { get; set; } = 5f;

    /// <summary>Radians of rotation per pixel of pointer travel.</summary>
    [EditorSetting("Look Sensitivity", Description = "Radians the view turns per pixel of pointer travel.")]
    [Range(0.0005f, 0.05f)]
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>Fraction of the move speed one scroll notch dollies by.</summary>
    [EditorSetting("Zoom Fraction", Description = "Fraction of the move speed one scroll notch travels.")]
    [Range(0.01f, 1f)]
    public float ZoomFraction { get; set; } = 0.1f;
}
