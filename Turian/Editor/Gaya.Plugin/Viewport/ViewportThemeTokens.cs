namespace Gaya.Plugin.Turian;

/// <summary>
/// The Scene view's theme tokens, declared with defaults so any theme can restyle the gizmo and viewport overlays.
/// Axis colors stay user settings.
/// </summary>
static class ViewportThemeTokens
{
    /// <summary>Transform handle under the pointer.</summary>
    public const string GizmoHover = "turian-gizmo-hover";

    /// <summary>Free-move center handle.</summary>
    public const string GizmoCenter = "turian-gizmo-center";

    /// <summary>Transform handle being dragged.</summary>
    public const string GizmoDrag = "turian-gizmo-drag";

    /// <summary>Disc behind the orientation cube.</summary>
    public const string OrientationBackground = "turian-orientation-bg";

    /// <summary>Disc behind the orientation cube while hovered.</summary>
    public const string OrientationBackgroundHover = "turian-orientation-bg-hover";

    /// <summary>Lit face of the orientation cube; other faces are shaded from it.</summary>
    public const string OrientationCube = "turian-orientation-cube";

    /// <summary>Outline of the camera preview inset.</summary>
    public const string PreviewBorder = "turian-preview-border";

    /// <summary>Declares every token with the colors the Scene view always used.</summary>
    /// <param name="themes">The host's token registry.</param>
    public static void Register(IThemeTokenRegistry themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        themes.Token(GizmoHover, "#ffde85");
        themes.Token(GizmoCenter, "#dbe3f2");
        themes.Token(GizmoDrag, "#ffffff");
        themes.Token(OrientationBackground, "rgba(31, 36, 43, 0.2196)");
        themes.Token(OrientationBackgroundHover, "rgba(31, 36, 43, 0.698)");
        themes.Token(OrientationCube, "#d1dbeb");
        themes.Token(PreviewBorder, "#ffffff");
    }

    /// <summary>A theme color as the gizmo renderer's normalized RGBA.</summary>
    /// <param name="theme">The active theme.</param>
    /// <param name="token">The token name.</param>
    /// <param name="fallback">Used when the theme lacks the token.</param>
    public static Vector4 Vector(ThemeTokens theme, string token, Vector4 fallback) =>
        theme.Colors.TryGetValue(token, out var color)
            ? new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)
            : fallback;
}
