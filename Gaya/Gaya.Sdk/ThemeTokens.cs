namespace Gaya.Sdk;

/// <summary>
/// The typed, immutable snapshot of the active theme: every color and metric the studio is drawn with, compiled from
/// the theme's <c>.pss</c> sheets whenever the theme changes. Panels read <see cref="Current"/> instead of holding
/// colors of their own, so switching a theme repaints everything on the next frame.
/// </summary>
/// <remarks>
/// <see cref="TextScale"/> and <see cref="Zoom"/> are the user's two size knobs, applied through <see cref="Text"/>
/// and <see cref="Scale"/>: text scales on its own, and everything a control measures scales with the zoom. The
/// property defaults are the built-in Dark theme, used until a theme has been compiled.
/// </remarks>
public sealed record ThemeTokens
{
    /// <summary>The stable theme id persisted in the appearance settings, such as <c>gaya.dark</c>.</summary>
    public string Id { get; init; } = "gaya.dark";

    /// <summary>The name shown in the View menu and the Appearance settings.</summary>
    public string Name { get; init; } = "Dark";

    /// <summary>Whether the theme is dark, light or high contrast.</summary>
    public ThemeKind Kind { get; init; } = ThemeKind.Dark;

    /// <summary>Whether this is a dark theme, for anything that has to pick a contrasting default.</summary>
    public bool IsDark => Kind is ThemeKind.Dark or ThemeKind.HighContrastDark;

    /// <summary>Window background behind the dock space (<c>$surface-window</c>).</summary>
    public Color Background { get; init; } = Color.FromArgb(255, 26, 26, 26);

    /// <summary>Panel body fill (<c>$surface-panel</c>).</summary>
    public Color Panel { get; init; } = Color.FromArgb(255, 35, 35, 35);

    /// <summary>Menu bar, panel header, tab strip and status bar fill (<c>$surface-chrome</c>).</summary>
    public Color Chrome { get; init; } = Color.FromArgb(255, 43, 43, 43);

    /// <summary>Fill of an input, a dropdown or any other editable box (<c>$surface-field</c>).</summary>
    public Color Field { get; init; } = Color.FromArgb(255, 22, 22, 22);

    /// <summary>Hover highlight (<c>$surface-hover</c>).</summary>
    public Color Hover { get; init; } = Color.FromArgb(255, 56, 56, 56);

    /// <summary>Pressed or engaged surface (<c>$surface-active</c>).</summary>
    public Color SurfaceActive { get; init; } = Color.FromArgb(255, 62, 62, 62);

    /// <summary>Popup, menu and tooltip fill (<c>$surface-popup</c>).</summary>
    public Color Popup { get; init; } = Color.FromArgb(255, 35, 35, 35);

    /// <summary>Central editor-area fill when nothing occupies it (<c>$surface-editor-area</c>).</summary>
    public Color EditorArea { get; init; } = Color.FromArgb(255, 16, 16, 16);

    /// <summary>Veil drawn over the workbench while it is blocked (<c>$scrim</c>).</summary>
    public Color Scrim { get; init; } = Color.FromArgb(90, 0, 0, 0);

    /// <summary>Drop-shadow color (<c>$shadow</c>).</summary>
    public Color Shadow { get; init; } = Color.FromArgb(77, 0, 0, 0);

    /// <summary>Panel and control border (<c>$border</c>).</summary>
    public Color Border { get; init; } = Color.FromArgb(255, 58, 58, 58);

    /// <summary>Border that has to stand out, such as a focused field (<c>$border-strong</c>).</summary>
    public Color BorderStrong { get; init; } = Color.FromArgb(255, 107, 107, 107);

    /// <summary>Separator line between groups (<c>$divider</c>).</summary>
    public Color Divider { get; init; } = Color.FromArgb(255, 58, 58, 58);

    /// <summary>Keyboard focus outline (<c>$focus-ring</c>).</summary>
    public Color FocusRing { get; init; } = Color.FromArgb(128, 140, 140, 140);

    /// <summary>Primary text (<c>$ink</c>).</summary>
    public Color Ink { get; init; } = Color.FromArgb(255, 214, 214, 214);

    /// <summary>Secondary text: labels, counts, descriptions (<c>$ink-dim</c>).</summary>
    public Color InkDim { get; init; } = Color.FromArgb(255, 144, 144, 144);

    /// <summary>Text of something switched off, such as an inactive node (<c>$ink-faint</c>).</summary>
    public Color InkFaint { get; init; } = Color.FromArgb(255, 107, 107, 107);

    /// <summary>Text drawn on an accent fill (<c>$ink-on-accent</c>).</summary>
    public Color InkOnAccent { get; init; } = Color.FromArgb(255, 0, 0, 0);

    /// <summary>Hyperlink text (<c>$link</c>).</summary>
    public Color Link { get; init; } = Color.FromArgb(255, 140, 140, 140);

    /// <summary>Focus ring, selection outline and anything the user is acting on (<c>$accent</c>).</summary>
    public Color Accent { get; init; } = Color.FromArgb(255, 140, 140, 140);

    /// <summary>Accent under the pointer (<c>$accent-hover</c>).</summary>
    public Color AccentHover { get; init; } = Color.FromArgb(255, 157, 157, 157);

    /// <summary>Fill behind a selected row or an engaged toolbar button, drawn under <see cref="Ink"/> (<c>$accent-fill</c>).</summary>
    public Color AccentFill { get; init; } = Color.FromArgb(255, 62, 62, 62);

    /// <summary>Selected text background (<c>$selection</c>).</summary>
    public Color Selection { get; init; } = Color.FromArgb(110, 140, 140, 140);

    /// <summary>Selected text color (<c>$selection-ink</c>).</summary>
    public Color SelectionInk { get; init; } = Color.FromArgb(255, 214, 214, 214);

    /// <summary>Errors, failures and anything that did not load (<c>$error</c>).</summary>
    public Color Error { get; init; } = Color.FromArgb(255, 214, 118, 118);

    /// <summary>Warnings and cancelled work (<c>$warning</c>).</summary>
    public Color Warning { get; init; } = Color.FromArgb(255, 235, 190, 110);

    /// <summary>Completed work and passing checks (<c>$success</c>).</summary>
    public Color Success { get; init; } = Color.FromArgb(255, 139, 197, 139);

    /// <summary>Neutral notices (<c>$info</c>).</summary>
    public Color Info { get; init; } = Color.FromArgb(255, 111, 168, 220);

    /// <summary>Suggestions and hints (<c>$hint</c>).</summary>
    public Color Hint { get; init; } = Color.FromArgb(255, 144, 144, 144);

    /// <summary>Folder rows in the asset browser (<c>$folder</c>).</summary>
    public Color Folder { get; init; } = Color.FromArgb(255, 226, 192, 118);

    /// <summary>Debug and trace log lines (<c>$log-debug</c>).</summary>
    public Color LogDebug { get; init; } = Color.FromArgb(255, 107, 107, 107);

    /// <summary>Information log lines (<c>$log-info</c>).</summary>
    public Color LogInfo { get; init; } = Color.FromArgb(255, 214, 214, 214);

    /// <summary>Warning log lines (<c>$log-warning</c>).</summary>
    public Color LogWarning { get; init; } = Color.FromArgb(255, 235, 190, 110);

    /// <summary>Error and critical log lines (<c>$log-error</c>).</summary>
    public Color LogError { get; init; } = Color.FromArgb(255, 214, 118, 118);

    /// <summary>The sixteen terminal colors <c>$ansi-0</c> … <c>$ansi-15</c>.</summary>
    public IReadOnlyList<Color> Ansi { get; init; } = [];

    /// <summary>Foreground, background and border of each <see cref="StatusKind"/>.</summary>
    public IReadOnlyDictionary<StatusKind, StatusColors> Statuses { get; init; } =
        new Dictionary<StatusKind, StatusColors>();

    /// <summary>Base body font size, before <see cref="TextScale"/> (<c>$font-size</c>).</summary>
    public float FontSize { get; init; } = 12f;

    /// <summary>Small text, such as captions and badges, before <see cref="TextScale"/> (<c>$font-size-small</c>).</summary>
    public float FontSizeSmall { get; init; } = 11f;

    /// <summary>Emphasized text before <see cref="TextScale"/> (<c>$font-size-large</c>).</summary>
    public float FontSizeLarge { get; init; } = 13f;

    /// <summary>Headings before <see cref="TextScale"/> (<c>$font-size-title</c>).</summary>
    public float FontSizeTitle { get; init; } = 16f;

    /// <summary>Menu-bar height, before <see cref="Zoom"/> (<c>$menu-height</c>).</summary>
    public float MenuHeight { get; init; } = 30f;

    /// <summary>Status-bar height, before <see cref="Zoom"/> (<c>$status-height</c>).</summary>
    public float StatusHeight { get; init; } = 24f;

    /// <summary>Panel header and dock tab height, before <see cref="Zoom"/> (<c>$header-height</c>).</summary>
    public float HeaderHeight { get; init; } = 24f;

    /// <summary>Height of one form row, before <see cref="Zoom"/> (<c>$row-height</c>).</summary>
    public float RowHeight { get; init; } = 20f;

    /// <summary>Gap between regions (<c>$gap</c>).</summary>
    public float Gap { get; init; } = 6f;

    /// <summary>Padding inside a control (<c>$padding</c>).</summary>
    public float Padding { get; init; } = 6f;

    /// <summary>Space between items in a row or a list (<c>$spacing</c>).</summary>
    public float Spacing { get; init; } = 4f;

    /// <summary>Corner radius of controls and panels (<c>$radius</c>).</summary>
    public float Radius { get; init; } = 4f;

    /// <summary>Corner radius of small elements such as badges (<c>$radius-small</c>).</summary>
    public float RadiusSmall { get; init; } = 3f;

    /// <summary>Border stroke width (<c>$border-width</c>).</summary>
    public float BorderWidth { get; init; } = 1f;

    /// <summary>Focus outline width (<c>$focus-ring-width</c>).</summary>
    public float FocusRingWidth { get; init; } = 2f;

    /// <summary>Multiplies every font size. The user's "Text Size" setting.</summary>
    public float TextScale { get; init; } = 1f;

    /// <summary>Multiplies every measured length. The user's "Zoom" setting.</summary>
    public float Zoom { get; init; } = 1f;

    /// <summary>Every color token of the compiled theme by name without <c>$</c>, plugin contributions included.</summary>
    public IReadOnlyDictionary<string, Color> Colors { get; init; } = new Dictionary<string, Color>();

    /// <summary>Every pixel-length token of the compiled theme by name without <c>$</c>.</summary>
    public IReadOnlyDictionary<string, float> Lengths { get; init; } = new Dictionary<string, float>();

    /// <summary>Every token's evaluated text by name without <c>$</c>, for values that are neither colors nor lengths.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>A font size with the user's text scale applied.</summary>
    /// <param name="size">The size the call site would use at scale 1.</param>
    /// <returns>The scaled size.</returns>
    public float Text(float size) => size * TextScale;

    /// <summary>A length with the user's zoom applied.</summary>
    /// <param name="length">The length the call site would use at zoom 1.</param>
    /// <returns>The scaled length.</returns>
    public float Scale(float length) => length * Zoom;

    /// <summary>A color token by name, such as <c>surface-high</c> or a plugin's own.</summary>
    /// <param name="token">Token name, with or without the leading <c>$</c>.</param>
    /// <param name="fallback">Returned when the theme defines no such color.</param>
    /// <returns>The token's color, or <paramref name="fallback"/>.</returns>
    public Color GetColor(string token, Color fallback = default) =>
        Colors.TryGetValue(Key(token), out var color) ? color : fallback;

    /// <summary>A pixel-length token by name, before <see cref="Zoom"/>.</summary>
    /// <param name="token">Token name, with or without the leading <c>$</c>.</param>
    /// <param name="fallback">Returned when the theme defines no such length.</param>
    /// <returns>The token's length, or <paramref name="fallback"/>.</returns>
    public float GetLength(string token, float fallback = 0f) =>
        Lengths.TryGetValue(Key(token), out var length) ? length : fallback;

    /// <summary>A token's evaluated text by name, such as a font family list.</summary>
    /// <param name="token">Token name, with or without the leading <c>$</c>.</param>
    /// <returns>The token's text, or <c>null</c> when the theme does not define it.</returns>
    public string? GetValue(string token) => Values.GetValueOrDefault(Key(token));

    /// <summary>Foreground, background and border of a status.</summary>
    /// <param name="kind">The status.</param>
    /// <returns>The status colors; ink on the panel when the theme does not define them.</returns>
    public StatusColors Status(StatusKind kind) =>
        Statuses.TryGetValue(kind, out var colors) ? colors : new StatusColors(Ink, Panel, Border);

    /// <summary>The theme compiled defaults: the built-in Dark theme.</summary>
    public static ThemeTokens Default { get; } = new();

    /// <summary>
    /// The theme everything draws with right now. An ambient value rather than an injected service: the drawers
    /// that need it are static, and every panel in the process shares one theme anyway.
    /// </summary>
    public static ThemeTokens Current { get; set; } = Default;

    static string Key(string token) => token.StartsWith('$') ? token[1..] : token;
}

/// <summary>Foreground, background and border colors of one status, such as an error or a modified file.</summary>
/// <param name="Color">Text and icon color.</param>
/// <param name="Background">Fill behind the status.</param>
/// <param name="Border">Outline around the status.</param>
public readonly record struct StatusColors(Color Color, Color Background, Color Border);

/// <summary>States with a color triplet in the theme: <c>$name</c>, <c>$name-bg</c> and <c>$name-border</c>.</summary>
public enum StatusKind
{
    /// <summary>Errors and failures.</summary>
    Error,
    /// <summary>Warnings.</summary>
    Warning,
    /// <summary>Success and passing checks.</summary>
    Success,
    /// <summary>Neutral information.</summary>
    Info,
    /// <summary>Hints and suggestions.</summary>
    Hint,
    /// <summary>An added file or line.</summary>
    Created,
    /// <summary>A changed file or line.</summary>
    Modified,
    /// <summary>A removed file or line.</summary>
    Deleted,
    /// <summary>A moved or renamed file.</summary>
    Renamed,
    /// <summary>A merge conflict.</summary>
    Conflict,
    /// <summary>An ignored file.</summary>
    Ignored,
}

/// <summary>The appearance family of a theme, from its <c>@const theme-kind</c>.</summary>
public enum ThemeKind
{
    /// <summary>Light ink on dark surfaces.</summary>
    Dark,
    /// <summary>Dark ink on light surfaces.</summary>
    Light,
    /// <summary>A dark theme tuned for maximum contrast.</summary>
    HighContrastDark,
    /// <summary>A light theme tuned for maximum contrast.</summary>
    HighContrastLight,
}
