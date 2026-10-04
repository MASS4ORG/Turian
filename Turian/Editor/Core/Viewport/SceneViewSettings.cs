namespace Turian.Editor.Core;

/// <summary>Controls the environment shown by the editor's Scene view.</summary>
[EditorSetting("Scene Viewer")]
public sealed class SceneViewSettings
{
    /// <summary>Gets or sets normalized linear RGB channels for the empty Scene background.</summary>
    [EditorSetting("Empty Sky Color", Description = "Background color where no scene geometry is drawn.")]
    public Vector3 EmptySkyColor { get; set; } = new(0.035f);
}
