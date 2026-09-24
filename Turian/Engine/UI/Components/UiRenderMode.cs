namespace Turian.Engine.UI;

/// <summary>Where a <see cref="UiDocumentComponent"/>'s output is drawn.</summary>
public enum UiRenderMode
{
    /// <summary>Full-screen, composited over everything after the scene renders. HUDs and menus.</summary>
    ScreenSpaceOverlay = 0,

    /// <summary>
    /// Full-screen, but rendered as if it sat a fixed distance in front of a camera — reserved for
    /// perspective/scale effects. Composited the same way as <see cref="ScreenSpaceOverlay"/> for now.
    /// </summary>
    ScreenSpaceCamera = 1,

    /// <summary>Rendered to its own texture and drawn on a quad at the node's transform. In-world panels.</summary>
    WorldSpace = 2,
}
