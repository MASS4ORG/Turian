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
    public Color Background { get; init; } = Color.FromArgb(255, 26, 26, 26);

    /// <summary>Panel body fill.</summary>
    public Color Panel { get; init; } = Color.FromArgb(255, 35, 35, 35);

    /// <summary>Menu bar, panel header, tab strip and status bar fill.</summary>
    public Color Chrome { get; init; } = Color.FromArgb(255, 43, 43, 43);

    /// <summary>Fill of an input, a dropdown or any other editable box.</summary>
    public Color Field { get; init; } = Color.FromArgb(255, 22, 22, 22);

    /// <summary>Hover highlight.</summary>
    public Color Hover { get; init; } = Color.FromArgb(255, 56, 56, 56);

    /// <summary>Panel and control border.</summary>
    public Color Border { get; init; } = Color.FromArgb(255, 58, 58, 58);

    /// <summary>Primary text.</summary>
    public Color Ink { get; init; } = Color.FromArgb(255, 214, 214, 214);

    /// <summary>Secondary text — labels, counts, descriptions.</summary>
    public Color InkDim { get; init; } = Color.FromArgb(255, 144, 144, 144);

    /// <summary>Text of something switched off, such as an inactive node.</summary>
    public Color InkFaint { get; init; } = Color.FromArgb(255, 107, 107, 107);

    /// <summary>Focus ring, selection outline and the color of anything the user is acting on.</summary>
    public Color Accent { get; init; } = Color.FromArgb(255, 140, 140, 140);

    /// <summary>Fill behind a selected row or an engaged toolbar button, drawn under <see cref="Ink"/>.</summary>
    public Color AccentFill { get; init; } = Color.FromArgb(255, 62, 62, 62);

    /// <summary>Central editor-area fill when nothing occupies it.</summary>
    public Color EditorArea { get; init; } = Color.FromArgb(255, 16, 16, 16);

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

    /// <summary>
    /// The studio's own dark theme, and the default: neutral greys with no color cast, so the chrome
    /// never fights game content for attention.
    /// </summary>
    public static StudioTheme Dark { get; } = new() { Name = "Dark" };

    /// <summary>The studio's own light theme: the same neutral greys flipped, the default light option.</summary>
    public static StudioTheme Light { get; } = new()
    {
        Name = "Light",
        IsDark = false,
        Background = Color.FromArgb(255, 229, 229, 229),
        Panel = Color.FromArgb(255, 247, 247, 247),
        Chrome = Color.FromArgb(255, 239, 239, 239),
        Field = Color.FromArgb(255, 255, 255, 255),
        Hover = Color.FromArgb(255, 224, 224, 224),
        Border = Color.FromArgb(255, 207, 207, 207),
        Ink = Color.FromArgb(255, 36, 36, 36),
        InkDim = Color.FromArgb(255, 95, 95, 95),
        InkFaint = Color.FromArgb(255, 143, 143, 143),
        Accent = Color.FromArgb(255, 61, 61, 61),
        AccentFill = Color.FromArgb(255, 220, 220, 220),
        EditorArea = Color.FromArgb(255, 223, 223, 223),
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

    /// <summary>Dracula: the well-known dark-blue ground with purple focus and pastel signal colors.</summary>
    public static StudioTheme Dracula { get; } = new()
    {
        Name = "Dracula",
        Background = Color.FromArgb(255, 40, 42, 54),
        Panel = Color.FromArgb(255, 33, 34, 44),
        Chrome = Color.FromArgb(255, 47, 50, 65),
        Field = Color.FromArgb(255, 27, 28, 37),
        Hover = Color.FromArgb(255, 68, 71, 90),
        Border = Color.FromArgb(255, 74, 78, 99),
        Ink = Color.FromArgb(255, 248, 248, 242),
        InkDim = Color.FromArgb(255, 176, 182, 201),
        InkFaint = Color.FromArgb(255, 98, 114, 164),
        Accent = Color.FromArgb(255, 189, 147, 249),
        AccentFill = Color.FromArgb(255, 65, 71, 99),
        EditorArea = Color.FromArgb(255, 27, 28, 36),
        Error = Color.FromArgb(255, 255, 85, 85),
        Warning = Color.FromArgb(255, 255, 184, 108),
        Folder = Color.FromArgb(255, 241, 250, 140),
    };

    /// <summary>Monokai: the classic Sublime Text palette, with Monokai Pro's pink as the accent.</summary>
    public static StudioTheme Monokai { get; } = new()
    {
        Name = "Monokai",
        Background = Color.FromArgb(255, 39, 40, 34),
        Panel = Color.FromArgb(255, 33, 34, 32),
        Chrome = Color.FromArgb(255, 49, 50, 44),
        Field = Color.FromArgb(255, 27, 28, 24),
        Hover = Color.FromArgb(255, 73, 72, 62),
        Border = Color.FromArgb(255, 62, 61, 53),
        Ink = Color.FromArgb(255, 248, 248, 242),
        InkDim = Color.FromArgb(255, 196, 196, 184),
        InkFaint = Color.FromArgb(255, 117, 113, 94),
        Accent = Color.FromArgb(255, 255, 97, 136),
        AccentFill = Color.FromArgb(255, 69, 68, 59),
        EditorArea = Color.FromArgb(255, 29, 30, 25),
        Error = Color.FromArgb(255, 249, 38, 114),
        Warning = Color.FromArgb(255, 230, 219, 116),
        Folder = Color.FromArgb(255, 253, 151, 31),
    };

    /// <summary>One Dark: Atom's editor palette — the classic #282C34 grey with its soft blue accent.</summary>
    public static StudioTheme OneDark { get; } = new()
    {
        Name = "One Dark",
        Background = Color.FromArgb(255, 33, 37, 43),
        Panel = Color.FromArgb(255, 40, 44, 52),
        Chrome = Color.FromArgb(255, 44, 49, 60),
        Field = Color.FromArgb(255, 27, 30, 35),
        Hover = Color.FromArgb(255, 58, 63, 75),
        Border = Color.FromArgb(255, 62, 68, 81),
        Ink = Color.FromArgb(255, 171, 178, 191),
        InkDim = Color.FromArgb(255, 130, 140, 153),
        InkFaint = Color.FromArgb(255, 92, 99, 112),
        Accent = Color.FromArgb(255, 97, 175, 239),
        AccentFill = Color.FromArgb(255, 44, 62, 82),
        EditorArea = Color.FromArgb(255, 24, 26, 31),
        Error = Color.FromArgb(255, 224, 108, 117),
        Warning = Color.FromArgb(255, 229, 192, 123),
        Folder = Color.FromArgb(255, 209, 154, 102),
    };

    /// <summary>Nord: the polar-night greys and frost blues of the Nord palette by Arctic Ice Studio.</summary>
    public static StudioTheme Nord { get; } = new()
    {
        Name = "Nord",
        Background = Color.FromArgb(255, 46, 52, 64),
        Panel = Color.FromArgb(255, 59, 66, 82),
        Chrome = Color.FromArgb(255, 67, 76, 94),
        Field = Color.FromArgb(255, 39, 44, 54),
        Hover = Color.FromArgb(255, 76, 86, 106),
        Border = Color.FromArgb(255, 76, 86, 106),
        Ink = Color.FromArgb(255, 216, 222, 233),
        InkDim = Color.FromArgb(255, 169, 178, 193),
        InkFaint = Color.FromArgb(255, 123, 136, 161),
        Accent = Color.FromArgb(255, 136, 192, 208),
        AccentFill = Color.FromArgb(255, 62, 76, 100),
        EditorArea = Color.FromArgb(255, 36, 41, 51),
        Error = Color.FromArgb(255, 191, 97, 106),
        Warning = Color.FromArgb(255, 235, 203, 139),
        Folder = Color.FromArgb(255, 208, 135, 112),
    };

    /// <summary>Gruvbox Dark: retro-groove warm earthy greys with an orange accent.</summary>
    public static StudioTheme Gruvbox { get; } = new()
    {
        Name = "Gruvbox",
        Background = Color.FromArgb(255, 40, 40, 40),
        Panel = Color.FromArgb(255, 50, 48, 47),
        Chrome = Color.FromArgb(255, 60, 56, 54),
        Field = Color.FromArgb(255, 29, 32, 33),
        Hover = Color.FromArgb(255, 80, 73, 69),
        Border = Color.FromArgb(255, 80, 73, 69),
        Ink = Color.FromArgb(255, 235, 219, 178),
        InkDim = Color.FromArgb(255, 189, 174, 147),
        InkFaint = Color.FromArgb(255, 146, 131, 116),
        Accent = Color.FromArgb(255, 254, 128, 25),
        AccentFill = Color.FromArgb(255, 94, 67, 39),
        EditorArea = Color.FromArgb(255, 26, 26, 24),
        Error = Color.FromArgb(255, 251, 73, 52),
        Warning = Color.FromArgb(255, 250, 189, 47),
        Folder = Color.FromArgb(255, 254, 128, 25),
    };

    /// <summary>Tokyo Night: Enkia's blue-lit Tokyo palette — deep navy chrome, bright blue-violet ink.</summary>
    public static StudioTheme TokyoNight { get; } = new()
    {
        Name = "Tokyo Night",
        Background = Color.FromArgb(255, 26, 27, 38),
        Panel = Color.FromArgb(255, 22, 22, 30),
        Chrome = Color.FromArgb(255, 31, 35, 53),
        Field = Color.FromArgb(255, 19, 21, 32),
        Hover = Color.FromArgb(255, 41, 46, 66),
        Border = Color.FromArgb(255, 47, 53, 73),
        Ink = Color.FromArgb(255, 192, 202, 245),
        InkDim = Color.FromArgb(255, 166, 174, 203),
        InkFaint = Color.FromArgb(255, 86, 95, 137),
        Accent = Color.FromArgb(255, 122, 162, 247),
        AccentFill = Color.FromArgb(255, 40, 52, 82),
        EditorArea = Color.FromArgb(255, 16, 16, 23),
        Error = Color.FromArgb(255, 247, 118, 142),
        Warning = Color.FromArgb(255, 224, 175, 104),
        Folder = Color.FromArgb(255, 255, 158, 100),
    };

    /// <summary>Catppuccin Mocha: the pastel-on-dark palette — warm dark grounds, lavender-blue ink.</summary>
    public static StudioTheme Catppuccin { get; } = new()
    {
        Name = "Catppuccin",
        Background = Color.FromArgb(255, 17, 17, 27),
        Panel = Color.FromArgb(255, 30, 30, 46),
        Chrome = Color.FromArgb(255, 24, 24, 37),
        Field = Color.FromArgb(255, 49, 50, 68),
        Hover = Color.FromArgb(255, 69, 71, 90),
        Border = Color.FromArgb(255, 65, 68, 88),
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

    /// <summary>Solarized Dark: Ethan Schoonover's classic — deep cyan-blue grounds with muted ink.</summary>
    public static StudioTheme SolarizedDark { get; } = new()
    {
        Name = "Solarized Dark",
        Background = Color.FromArgb(255, 0, 43, 54),
        Panel = Color.FromArgb(255, 7, 54, 66),
        Chrome = Color.FromArgb(255, 10, 72, 84),
        Field = Color.FromArgb(255, 0, 37, 46),
        Hover = Color.FromArgb(255, 16, 81, 95),
        Border = Color.FromArgb(255, 19, 79, 92),
        Ink = Color.FromArgb(255, 147, 161, 161),
        InkDim = Color.FromArgb(255, 101, 123, 131),
        InkFaint = Color.FromArgb(255, 88, 110, 117),
        Accent = Color.FromArgb(255, 38, 139, 210),
        AccentFill = Color.FromArgb(255, 23, 71, 92),
        EditorArea = Color.FromArgb(255, 0, 33, 43),
        Error = Color.FromArgb(255, 220, 50, 47),
        Warning = Color.FromArgb(255, 181, 137, 0),
        Folder = Color.FromArgb(255, 203, 75, 22),
    };

    /// <summary>Solarized Light: the cream-paper half of the Solarized pair.</summary>
    public static StudioTheme SolarizedLight { get; } = new()
    {
        Name = "Solarized Light",
        IsDark = false,
        Background = Color.FromArgb(255, 238, 232, 213),
        Panel = Color.FromArgb(255, 253, 246, 227),
        Chrome = Color.FromArgb(255, 231, 225, 204),
        Field = Color.FromArgb(255, 255, 251, 238),
        Hover = Color.FromArgb(255, 221, 214, 191),
        Border = Color.FromArgb(255, 201, 193, 165),
        Ink = Color.FromArgb(255, 88, 110, 117),
        InkDim = Color.FromArgb(255, 101, 123, 131),
        InkFaint = Color.FromArgb(255, 147, 161, 161),
        Accent = Color.FromArgb(255, 38, 139, 210),
        AccentFill = Color.FromArgb(255, 211, 229, 242),
        EditorArea = Color.FromArgb(255, 245, 240, 220),
        Error = Color.FromArgb(255, 220, 50, 47),
        Warning = Color.FromArgb(255, 181, 137, 0),
        Folder = Color.FromArgb(255, 203, 75, 22),
    };

    /// <summary>Ayu Dark: Ike Ku's dark theme — near-black blue grounds with its golden-orange accent.</summary>
    public static StudioTheme AyuDark { get; } = new()
    {
        Name = "Ayu Dark",
        Background = Color.FromArgb(255, 10, 14, 20),
        Panel = Color.FromArgb(255, 13, 16, 23),
        Chrome = Color.FromArgb(255, 21, 26, 36),
        Field = Color.FromArgb(255, 7, 10, 16),
        Hover = Color.FromArgb(255, 31, 36, 48),
        Border = Color.FromArgb(255, 35, 40, 52),
        Ink = Color.FromArgb(255, 191, 191, 191),
        InkDim = Color.FromArgb(255, 138, 144, 153),
        InkFaint = Color.FromArgb(255, 98, 106, 115),
        Accent = Color.FromArgb(255, 230, 182, 115),
        AccentFill = Color.FromArgb(255, 58, 46, 27),
        EditorArea = Color.FromArgb(255, 4, 7, 12),
        Error = Color.FromArgb(255, 255, 51, 51),
        Warning = Color.FromArgb(255, 255, 180, 84),
        Folder = Color.FromArgb(255, 255, 143, 64),
    };

    /// <summary>Ayu Light: the bright half of the Ayu pair — white panels, warm grey ink, orange accent.</summary>
    public static StudioTheme AyuLight { get; } = new()
    {
        Name = "Ayu Light",
        IsDark = false,
        Background = Color.FromArgb(255, 240, 240, 240),
        Panel = Color.FromArgb(255, 250, 250, 250),
        Chrome = Color.FromArgb(255, 235, 235, 235),
        Field = Color.FromArgb(255, 255, 255, 255),
        Hover = Color.FromArgb(255, 225, 225, 225),
        Border = Color.FromArgb(255, 214, 214, 214),
        Ink = Color.FromArgb(255, 92, 97, 102),
        InkDim = Color.FromArgb(255, 108, 113, 118),
        InkFaint = Color.FromArgb(255, 138, 145, 153),
        Accent = Color.FromArgb(255, 255, 153, 64),
        AccentFill = Color.FromArgb(255, 255, 221, 184),
        EditorArea = Color.FromArgb(255, 234, 234, 234),
        Error = Color.FromArgb(255, 230, 80, 80),
        Warning = Color.FromArgb(255, 242, 151, 24),
        Folder = Color.FromArgb(255, 255, 153, 64),
    };

    /// <summary>Every theme the studio ships with, in the order the View menu lists them.</summary>
    public static IReadOnlyList<StudioTheme> BuiltIn { get; } =
    [
        Dark, Light, DarkContrast,
        Dracula, Monokai, OneDark, Nord, Gruvbox, TokyoNight, Catppuccin,
        SolarizedDark, SolarizedLight, AyuDark, AyuLight,
    ];

    /// <summary>
    /// The theme everything draws with right now. An ambient value rather than an injected service:
    /// the drawers that need it are static, and every panel in the process shares one theme anyway.
    /// </summary>
    public static StudioTheme Current { get; set; } = Dark;
}
