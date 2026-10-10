using System.Globalization;
using System.Text.RegularExpressions;

namespace Gaya.Host;

/// <summary>
/// Holds the committed color theme and the user's size scales, compiles the active sheet stack, and publishes the
/// result through <see cref="ThemeTokens.Current"/> so every panel draws with it.
/// </summary>
/// <remarks>
/// The stack is, lowest priority first: plugin token contributions, the theme sheet (with its imports), generated
/// user tokens, then the user override sheet <c>~/.gaya/theme.user.pss</c>. A preview is deliberately not sticky:
/// <see cref="PreviewColorTheme"/> marks the frame it was called on, and <see cref="EndFrame"/> drops anything
/// that was not renewed.
/// </remarks>
public sealed partial class ThemeService : IThemeService
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    readonly ILogger log;
    readonly Dictionary<string, string> contributions = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> userTokens = new(StringComparer.Ordinal);
    readonly Dictionary<string, Compiled> compiled = new(StringComparer.OrdinalIgnoreCase);
    readonly Stopwatch pollClock = Stopwatch.StartNew();

    readonly Selection color = new(ThemeCatalog.DefaultColorTheme);
    readonly Selection look = new(ThemeCatalog.DefaultLook);
    readonly Selection icons = new(ThemeCatalog.DefaultIconTheme);
    readonly Selection[] selections;
    float textSize = ThemeTokens.Default.FontSize;
    float zoom = 1f;
    long fingerprint;
    DateTime userSheetStamp;
    string? reportedError;
    IReadOnlyList<string>? queuedBrickFolders;

    /// <summary>A compiled theme: its snapshot, the sheets Guinevere widgets resolve against and the look's base.</summary>
    sealed record Compiled(ThemeTokens Tokens, IReadOnlyList<StyleSheet> Sheets, StyleSheet? Look,
        ThemeDiagnostic? Skipped);

    /// <summary>One independent choice: the committed id and the id shown while a menu row is hovered.</summary>
    sealed class Selection(string defaultId)
    {
        public string Committed { get; set; } = defaultId;
        public string? Preview { get; set; }
        public bool Renewed { get; set; }
        public string Shown => Preview ?? Committed;
    }

    /// <summary>Creates the service over the built-in and user themes and publishes the default.</summary>
    /// <param name="log">Receives theme errors, located as <c>file:line:column</c>.</param>
    /// <param name="catalog">Where themes are found; the default catalog when null.</param>
    /// <param name="userSheetPath">The user override sheet; <c>~/.gaya/theme.user.pss</c> when null.</param>
    public ThemeService(ILogger? log = null, ThemeCatalog? catalog = null, string? userSheetPath = null)
    {
        this.log = log ?? NullLogger.Instance;
        selections = [color, look, icons];
        Catalog = catalog ?? new ThemeCatalog(this.log);
        UserSheetPath = userSheetPath ?? UserConfigPath.For("theme.user.pss");
        fingerprint = Catalog.Fingerprint();
        userSheetStamp = Stamp(UserSheetPath);
        Publish();
    }

    /// <summary>Where themes are found.</summary>
    public ThemeCatalog Catalog { get; }

    /// <summary>The user override sheet, layered above every theme.</summary>
    public string UserSheetPath { get; }

    /// <inheritdoc />
    public IReadOnlyList<ThemeInfo> ColorThemes => Catalog.ColorThemes;

    /// <inheritdoc />
    public IReadOnlyList<ThemeInfo> Looks => Catalog.Looks;

    /// <inheritdoc />
    public IReadOnlyList<ThemeInfo> IconThemes => Catalog.IconThemes;

    /// <inheritdoc />
    public ThemeTokens Current { get; private set; } = ThemeTokens.Default;

    /// <summary>The sheets behind <see cref="Current"/>, lowest priority first, for <c>gui.StyleSheets</c>.</summary>
    public IReadOnlyList<StyleSheet> Sheets { get; private set; } = [];

    /// <summary>
    /// The showing look's complete base sheet for <c>ExcaliburStyles.SetBaseSheet</c>, or <c>null</c> for Guinevere's
    /// own default look.
    /// </summary>
    public StyleSheet? Look { get; private set; }

    /// <summary>Incremented whenever <see cref="Sheets"/> or <see cref="Look"/> changes.</summary>
    public int SheetsVersion { get; private set; }

    /// <inheritdoc />
    public string CommittedColorTheme => color.Committed;

    /// <inheritdoc />
    public string CommittedLook => look.Committed;

    /// <inheritdoc />
    public string CommittedIconTheme => icons.Committed;

    /// <inheritdoc />
    public ThemeDiagnostic? Diagnostic { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public ThemeInfo Register(ThemeSource source)
    {
        var info = Catalog.Register(source);
        Invalidate();
        return info;
    }

    /// <inheritdoc />
    [Obsolete("Register a .pss ThemeSource instead; StudioTheme is removed in the next release.")]
    public void Register(StudioTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Register(new ThemeSource(LegacyThemeSheet.From(theme), theme.Name));
    }

    /// <inheritdoc />
    public void Token(string name, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (!TokenName().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a token name; use letters, digits, '-' and '_'.", nameof(name));
        var declaration = $"${name} = {defaultValue};";
        StyleSheet.Parse(declaration, new StyleSheetOptions { SourceName = name });
        contributions[name] = defaultValue;
        Invalidate();
    }

    /// <summary>
    /// Sets a token above the theme and below the user override sheet, such as a font chosen in the settings.
    /// A <c>null</c> value removes it.
    /// </summary>
    /// <param name="name">Token name without <c>$</c>.</param>
    /// <param name="value">The <c>.pss</c> value, or <c>null</c>.</param>
    public void SetUserToken(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (userTokens.GetValueOrDefault(name) == value) return;
        if (value is null) userTokens.Remove(name);
        else userTokens[name] = value;
        Invalidate();
    }

    /// <inheritdoc />
    public void ApplyColorTheme(string id) => Apply(color, id);

    /// <inheritdoc />
    public void PreviewColorTheme(string id) => Preview(color, id);

    /// <inheritdoc />
    public void ApplyLook(string id) => Apply(look, id);

    /// <inheritdoc />
    public void PreviewLook(string id) => Preview(look, id);

    /// <inheritdoc />
    public void ApplyIconTheme(string id) => Apply(icons, id);

    /// <inheritdoc />
    public void PreviewIconTheme(string id) => Preview(icons, id);

    /// <inheritdoc />
    public string? DefaultColorThemeFor(string lookId)
    {
        ArgumentNullException.ThrowIfNull(lookId);
        if (Catalog.Find(lookId) is not { Category: ThemeCategories.Look } info) return null;

        var dark = Catalog.Find(color.Committed)?.Kind is null or ThemeKind.Dark or ThemeKind.HighContrastDark;
        var suggested = dark ? info.DefaultDarkTheme : info.DefaultLightTheme;
        return suggested is not null && Catalog.Find(suggested) is { Category: ThemeCategories.ColorTheme } theme
            ? theme.Id
            : null;
    }

    void Apply(Selection selection, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var resolved = Catalog.Find(id)?.Id ?? id;
        if (string.Equals(selection.Committed, resolved, StringComparison.Ordinal) && selection.Preview is null) return;

        selection.Committed = resolved;
        selection.Preview = null;
        Publish();
    }

    void Preview(Selection selection, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        selection.Renewed = true;

        if (Catalog.Find(id) is not { } theme || string.Equals(selection.Preview, theme.Id, StringComparison.Ordinal))
            return;

        selection.Preview = theme.Id;
        Publish();
    }

    /// <inheritdoc />
    public void SetScale(float requestedTextSize, float requestedZoom)
    {
        if (Math.Abs(requestedTextSize - textSize) < 0.001f
            && Math.Abs(requestedZoom - zoom) < 0.001f) return;

        textSize = requestedTextSize;
        zoom = requestedZoom;
        Publish();
    }

    /// <summary>
    /// Drops a preview no one renewed this frame and, at most once a second, reloads sheets changed on disk. Called
    /// by the workbench once per drawn frame.
    /// </summary>
    public void EndFrame()
    {
        if (Interlocked.Exchange(ref queuedBrickFolders, null) is { } folders) SetBrickFolders(folders);

        if (pollClock.Elapsed >= PollInterval)
        {
            pollClock.Restart();
            ReloadIfChanged();
        }

        var dropped = false;
        foreach (var selection in selections)
        {
            var renewed = selection.Renewed;
            selection.Renewed = false;
            if (renewed || selection.Preview is null) continue;

            selection.Preview = null;
            dropped = true;
        }
        if (dropped) Publish();
    }

    /// <summary>Rescans the themes and recompiles when a user or brick sheet, or the override sheet, changed.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool ReloadIfChanged()
    {
        var files = Catalog.Fingerprint();
        var userSheet = Stamp(UserSheetPath);
        if (files == fingerprint && userSheet == userSheetStamp) return false;

        fingerprint = files;
        userSheetStamp = userSheet;
        Catalog.Refresh();
        Invalidate();
        return true;
    }

    /// <summary>Sets the brick <c>Themes</c> folders, rescans, and applies a committed theme that just appeared.</summary>
    /// <param name="folders">Absolute folders.</param>
    public void SetBrickFolders(IEnumerable<string> folders)
    {
        Catalog.SetBrickFolders(folders);
        fingerprint = Catalog.Fingerprint();
        Invalidate();
    }

    /// <summary>Sets the brick folders from any thread; the next <see cref="EndFrame"/> rescans with them.</summary>
    /// <param name="folders">Absolute folders.</param>
    public void QueueBrickFolders(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        Volatile.Write(ref queuedBrickFolders, folders);
    }

    /// <summary>Drops every compiled theme and republishes, after a source or a contribution changed.</summary>
    void Invalidate()
    {
        compiled.Clear();
        Publish();
    }

    void Publish()
    {
        var shown = Catalog.Find(color.Shown) ?? Catalog.Find(ThemeCatalog.DefaultColorTheme);
        var shownLook = Catalog.Find(look.Shown) is { Category: ThemeCategories.Look } l ? l : null;
        var shownIcons = Catalog.Find(icons.Shown) is { Category: ThemeCategories.IconTheme } i
            ? i
            : Catalog.Find(ThemeCatalog.DefaultIconTheme);
        var result = shown is null ? null : Compile(shown, shownLook, shownIcons);
        if (result is null && Sheets.Count == 0 && shown?.Id != ThemeCatalog.DefaultColorTheme
            && Catalog.Find(ThemeCatalog.DefaultColorTheme) is { } fallback)
            result = Compile(fallback, shownLook, shownIcons);

        if (result is not null && !ReferenceEquals(result.Sheets, Sheets))
        {
            Sheets = result.Sheets;
            Look = result.Look;
            SheetsVersion++;
        }

        var tokens = result?.Tokens ?? Current;
        Current = tokens with { TextScale = textSize / tokens.FontSize, Zoom = zoom };
        ThemeTokens.Current = Current;
        Changed?.Invoke();
    }

    /// <summary>
    /// Compiles a color theme with a look and an icon theme, or returns <c>null</c> and reports the located error. A
    /// look or icon theme that cannot be loaded is reported and skipped, so it never costs the user their colors.
    /// </summary>
    Compiled? Compile(ThemeInfo info, ThemeInfo? lookInfo, ThemeInfo? iconInfo)
    {
        var key = $"{info.Id}|{lookInfo?.Id}|{iconInfo?.Id}";
        if (compiled.TryGetValue(key, out var cached))
        {
            Report(cached.Skipped);
            return cached;
        }

        try
        {
            ThemeDiagnostic? skipped = null;
            var lookSheet = LoadLook(lookInfo, ref skipped);
            var iconSheet = LoadSheet(iconInfo, ref skipped);
            var iconBase = iconInfo is not null && iconInfo.Id != ThemeCatalog.FallbackIconTheme
                ? LoadSheet(Catalog.Find(ThemeCatalog.FallbackIconTheme), ref skipped)
                : null;

            var sheets = new StyleSheetCollection();
            if (contributions.Count > 0) sheets.Add(Catalog.Parse(Declarations(contributions), "plugin tokens"));
            sheets.Add(Catalog.Load(info.Id));
            if (iconBase is not null) sheets.Add(iconBase);
            if (iconSheet is not null) sheets.Add(iconSheet);
            if (userTokens.Count > 0) sheets.Add(Catalog.Parse(Declarations(userTokens), "user settings"));
            if (File.Exists(UserSheetPath))
                sheets.Add(Catalog.Parse(File.ReadAllText(UserSheetPath), UserSheetPath, new Uri(UserSheetPath)));

            var problems = new List<string>();
            var tokens = ThemeCompiler.Compile(sheets, info, problems);
            foreach (var problem in problems) log.LogDebug("Theme {Theme}: {Problem}", info.Id, problem);

            var result = new Compiled(tokens, [.. sheets], lookSheet, skipped);
            compiled[key] = result;
            Report(skipped);
            return result;
        }
        catch (StyleSheetException ex)
        {
            Report(new ThemeDiagnostic(ex.Message, ex.SourceName, ex.Line, ex.Column));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(new ThemeDiagnostic(ex.Message, UserSheetPath, 0, 0));
        }
        return null;
    }

    /// <summary>
    /// Loads a look as Guinevere's base sheet. The default look and any look that fails to parse or to validate as a
    /// complete base sheet yield <c>null</c>, which leaves Guinevere's own default in place.
    /// </summary>
    StyleSheet? LoadLook(ThemeInfo? info, ref ThemeDiagnostic? skipped)
    {
        if (info is null || info.Id == ThemeCatalog.DefaultLook) return null;

        var sheet = LoadSheet(info, ref skipped);
        if (sheet is null) return null;

        var errors = ExcaliburStyles.ValidateBaseSheet(sheet);
        if (errors.Count == 0) return sheet;

        skipped = new ThemeDiagnostic($"Look {info.Id} is not a complete base sheet: {string.Join("; ", errors)}",
            info.Path ?? info.Id, 0, 0);
        return null;
    }

    /// <summary>Loads an optional sheet, recording a located error in <paramref name="skipped"/> when it fails.</summary>
    StyleSheet? LoadSheet(ThemeInfo? info, ref ThemeDiagnostic? skipped)
    {
        if (info is null) return null;

        try
        {
            return Catalog.Load(info.Id);
        }
        catch (StyleSheetException ex)
        {
            skipped = new ThemeDiagnostic(ex.Message, ex.SourceName, ex.Line, ex.Column);
            return null;
        }
    }

    /// <summary>Records the error that kept the last valid theme, logging each distinct one once.</summary>
    void Report(ThemeDiagnostic? diagnostic)
    {
        Diagnostic = diagnostic;
        if (diagnostic is null)
        {
            reportedError = null;
            return;
        }

        if (diagnostic.Message == reportedError) return;
        reportedError = diagnostic.Message;
        log.LogError("Theme error: {Message}", diagnostic.Message);
    }

    static string Declarations(IReadOnlyDictionary<string, string> tokens) =>
        string.Join('\n', tokens.Select(token => string.Create(CultureInfo.InvariantCulture,
            $"${token.Key} = {token.Value};")));

    static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex TokenName();
}
