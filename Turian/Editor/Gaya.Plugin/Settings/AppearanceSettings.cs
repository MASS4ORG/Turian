namespace Gaya.Plugin.Turian;

/// <summary>
/// How the studio looks: the two size knobs, plus the chosen theme. The theme itself is picked from
/// View / Themes rather than typed here, so it is hidden from the page but still persisted with it.
/// </summary>
[EditorSetting("Appearance", Description = "Theme and the size of the editor's own interface.")]
public sealed class AppearanceSettings
{
    int textSize = 12;

    /// <summary>The name of the committed <c>StudioTheme</c>.</summary>
    [HideInEditor]
    public string Theme { get; set; } = "Dark";

    /// <summary>The editor's base text size in points.</summary>
    [EditorSetting("Text Size", Description = "Base editor text size in points.")]
    [Range(9, 24)]
    public int TextSize
    {
        get => textSize;
        set => textSize = value is < 9 or > 24 ? 12 : value;
    }

    /// <summary>Multiplies everything the editor measures — rows, tabs, buttons, toolbars.</summary>
    [EditorSetting("Zoom", Description = "Scales what the editor measures: button, row and tab heights.")]
    [Range(0.6f, 2f)]
    public float Zoom { get; set; } = 1f;
}
