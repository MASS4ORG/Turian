namespace Gaya.Sdk;

/// <summary>
/// A named set of colors and metrics the whole studio is drawn with — chrome, dock space, panels and
/// the controls inside them. Panels read <see cref="Current"/> instead of holding colors of their
/// own, so switching a theme repaints everything on the next frame.
/// </summary>
/// <remarks>
/// <see cref="TextScale"/> and <see cref="Zoom"/> are the user's two size knobs, applied through
/// <see cref="Text"/> and <see cref="Scale"/>: text scales on its own, and everything a control
/// measures — row, tab and button heights — scales with the zoom.
/// </remarks>
public sealed record StudioTheme
{
    /// <summary>The name shown in the View menu and persisted in the appearance settings.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this is a dark theme, for anything that has to pick a contrasting default.</summary>
    public bool IsDark { get; init; } = true;

    /// <summary>Window background, behind the dock space.</summary>
    public Color Background { get; init; } = Color.FromArgb(255, 27, 30, 36);

    /// <summary>Panel body fill.</summary>
    public Color Panel { get; init; } = Color.FromArgb(255, 35, 39, 46);

    /// <summary>Menu bar, panel header, tab strip and status bar fill.</summary>
    public Color Chrome { get; init; } = Color.FromArgb(255, 43, 47, 55);

    /// <summary>Fill of an input, a dropdown or any other editable box.</summary>
    public Color Field { get; init; } = Color.FromArgb(255, 28, 31, 37);

    /// <summary>Hover highlight.</summary>
    public Color Hover { get; init; } = Color.FromArgb(255, 55, 60, 70);

    /// <summary>Panel and control border.</summary>
    public Color Border { get; init; } = Color.FromArgb(255, 51, 56, 66);

    /// <summary>Primary text.</summary>
    public Color Ink { get; init; } = Color.FromArgb(255, 215, 218, 224);

    /// <summary>Secondary text — labels, counts, descriptions.</summary>
    public Color InkDim { get; init; } = Color.FromArgb(255, 139, 146, 156);

    /// <summary>Text of something switched off, such as an inactive node.</summary>
    public Color InkFaint { get; init; } = Color.FromArgb(255, 105, 111, 120);

    /// <summary>Focus ring, selection outline and the color of anything the user is acting on.</summary>
    public Color Accent { get; init; } = Color.FromArgb(255, 84, 143, 224);

    /// <summary>Fill behind a selected row or an engaged toolbar button, drawn under <see cref="Ink"/>.</summary>
    public Color AccentFill { get; init; } = Color.FromArgb(255, 62, 95, 138);

    /// <summary>Central editor-area fill when nothing occupies it.</summary>
    public Color EditorArea { get; init; } = Color.FromArgb(255, 16, 18, 22);

    /// <summary>Errors, failures and anything that did not load.</summary>
    public Color Error { get; init; } = Color.FromArgb(255, 214, 118, 118);

    /// <summary>Warnings and cancelled work.</summary>
    public Color Warning { get; init; } = Color.FromArgb(255, 235, 190, 110);

    /// <summary>Folder rows in the asset browser.</summary>
    public Color Folder { get; init; } = Color.FromArgb(255, 226, 192, 118);

    /// <summary>Base body font size, before <see cref="TextScale"/>.</summary>
    public float FontSize { get; init; } = 12f;

    /// <summary>Menu-bar height, before <see cref="Zoom"/>.</summary>
    public float MenuHeight { get; init; } = 30f;

    /// <summary>Status-bar height, before <see cref="Zoom"/>.</summary>
    public float StatusHeight { get; init; } = 24f;

    /// <summary>Panel header and dock tab height, before <see cref="Zoom"/>.</summary>
    public float HeaderHeight { get; init; } = 24f;

    /// <summary>Height of one form row, before <see cref="Zoom"/>.</summary>
    public float RowHeight { get; init; } = 20f;

    /// <summary>Left dock column width.</summary>
    public float LeftWidth { get; init; } = 300f;

    /// <summary>Right dock column width.</summary>
    public float RightWidth { get; init; } = 340f;

    /// <summary>Bottom dock row height.</summary>
    public float BottomHeight { get; init; } = 240f;

    /// <summary>Gap between regions.</summary>
    public float Gap { get; init; } = 6f;

    /// <summary>Multiplies every font size. The user's "Text Size" setting.</summary>
    public float TextScale { get; init; } = 1f;

    /// <summary>Multiplies every measured length. The user's "Zoom" setting.</summary>
    public float Zoom { get; init; } = 1f;

    /// <summary>A font size with the user's text scale applied.</summary>
    /// <param name="size">The size the call site would use at scale 1.</param>
    /// <returns>The scaled size.</returns>
    public float Text(float size) => size * TextScale;

    /// <summary>A length with the user's zoom applied.</summary>
    /// <param name="length">The length the call site would use at zoom 1.</param>
    /// <returns>The scaled length.</returns>
    public float Scale(float length) => length * Zoom;

    /// <summary>The studio's own dark theme, and the default.</summary>
    public static StudioTheme Dark { get; } = new() { Name = "Dark" };

    /// <summary>A light theme for bright rooms.</summary>
    public static StudioTheme Light { get; } = new()
    {
        Name = "Light",
        IsDark = false,
        Background = Color.FromArgb(255, 238, 239, 241),
        Panel = Color.FromArgb(255, 249, 249, 250),
        Chrome = Color.FromArgb(255, 231, 232, 235),
        Field = Color.FromArgb(255, 255, 255, 255),
        Hover = Color.FromArgb(255, 219, 222, 227),
        Border = Color.FromArgb(255, 199, 203, 209),
        Ink = Color.FromArgb(255, 32, 34, 38),
        InkDim = Color.FromArgb(255, 95, 100, 107),
        InkFaint = Color.FromArgb(255, 143, 148, 155),
        Accent = Color.FromArgb(255, 0, 98, 197),
        AccentFill = Color.FromArgb(255, 183, 208, 240),
        EditorArea = Color.FromArgb(255, 224, 226, 229),
        Error = Color.FromArgb(255, 176, 48, 48),
        Warning = Color.FromArgb(255, 155, 108, 12),
        Folder = Color.FromArgb(255, 176, 138, 46),
    };

    /// <summary>A high-contrast dark theme: black grounds, white ink, visible borders.</summary>
    public static StudioTheme DarkContrast { get; } = new()
    {
        Name = "Dark Contrast",
        Background = Color.FromArgb(255, 0, 0, 0),
        Panel = Color.FromArgb(255, 0, 0, 0),
        Chrome = Color.FromArgb(255, 14, 14, 14),
        Field = Color.FromArgb(255, 0, 0, 0),
        Hover = Color.FromArgb(255, 44, 44, 44),
        Border = Color.FromArgb(255, 118, 118, 118),
        Ink = Color.FromArgb(255, 255, 255, 255),
        InkDim = Color.FromArgb(255, 214, 214, 214),
        InkFaint = Color.FromArgb(255, 163, 163, 163),
        Accent = Color.FromArgb(255, 0, 180, 255),
        AccentFill = Color.FromArgb(255, 0, 82, 148),
        EditorArea = Color.FromArgb(255, 0, 0, 0),
        Error = Color.FromArgb(255, 255, 128, 128),
        Warning = Color.FromArgb(255, 255, 212, 92),
        Folder = Color.FromArgb(255, 255, 222, 128),
    };

    /// <summary>The JetBrains greys.</summary>
    public static StudioTheme Darcula { get; } = new()
    {
        Name = "Darcula",
        Background = Color.FromArgb(255, 60, 63, 65),
        Panel = Color.FromArgb(255, 49, 51, 53),
        Chrome = Color.FromArgb(255, 60, 63, 65),
        Field = Color.FromArgb(255, 69, 73, 74),
        Hover = Color.FromArgb(255, 78, 82, 84),
        Border = Color.FromArgb(255, 85, 85, 85),
        Ink = Color.FromArgb(255, 187, 187, 187),
        InkDim = Color.FromArgb(255, 150, 150, 150),
        InkFaint = Color.FromArgb(255, 120, 120, 120),
        Accent = Color.FromArgb(255, 104, 151, 187),
        AccentFill = Color.FromArgb(255, 75, 110, 175),
        EditorArea = Color.FromArgb(255, 43, 43, 43),
        Error = Color.FromArgb(255, 255, 107, 104),
        Warning = Color.FromArgb(255, 255, 198, 109),
        Folder = Color.FromArgb(255, 237, 187, 108),
    };

    /// <summary>Catppuccin Mocha.</summary>
    public static StudioTheme Catppuccin { get; } = new()
    {
        Name = "Catppuccin",
        Background = Color.FromArgb(255, 24, 24, 37),
        Panel = Color.FromArgb(255, 30, 30, 46),
        Chrome = Color.FromArgb(255, 17, 17, 27),
        Field = Color.FromArgb(255, 49, 50, 68),
        Hover = Color.FromArgb(255, 69, 71, 90),
        Border = Color.FromArgb(255, 49, 50, 68),
        Ink = Color.FromArgb(255, 205, 214, 244),
        InkDim = Color.FromArgb(255, 166, 173, 200),
        InkFaint = Color.FromArgb(255, 108, 112, 134),
        Accent = Color.FromArgb(255, 137, 180, 250),
        AccentFill = Color.FromArgb(255, 58, 74, 113),
        EditorArea = Color.FromArgb(255, 17, 17, 27),
        Error = Color.FromArgb(255, 243, 139, 168),
        Warning = Color.FromArgb(255, 249, 226, 175),
        Folder = Color.FromArgb(255, 250, 179, 135),
    };

    /// <summary>Every theme the studio ships with, in the order the View menu lists them.</summary>
    public static IReadOnlyList<StudioTheme> BuiltIn { get; } = [Dark, Light, DarkContrast, Darcula, Catppuccin];

    /// <summary>
    /// The theme everything draws with right now. An ambient value rather than an injected service:
    /// the drawers that need it are static, and every panel in the process shares one theme anyway.
    /// </summary>
    public static StudioTheme Current { get; set; } = Dark;
}
