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

    string committed = ThemeCatalog.DefaultColorTheme;
    string? preview;
    bool previewRenewed;
    float textSize = ThemeTokens.Default.FontSize;
    float zoom = 1f;
    long fingerprint;
    DateTime userSheetStamp;
    string? reportedError;
    IReadOnlyList<string>? queuedBrickFolders;

    /// <summary>A compiled theme: its snapshot and the sheets Guinevere widgets resolve against.</summary>
    sealed record Compiled(ThemeTokens Tokens, IReadOnlyList<StyleSheet> Sheets);

    /// <summary>Creates the service over the built-in and user themes and publishes the default.</summary>
    /// <param name="log">Receives theme errors, located as <c>file:line:column</c>.</param>
    /// <param name="catalog">Where themes are found; the default catalog when null.</param>
    /// <param name="userSheetPath">The user override sheet; <c>~/.gaya/theme.user.pss</c> when null.</param>
    public ThemeService(ILogger? log = null, ThemeCatalog? catalog = null, string? userSheetPath = null)
    {
        this.log = log ?? NullLogger.Instance;
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
    public ThemeTokens Current { get; private set; } = ThemeTokens.Default;

    /// <summary>The sheets behind <see cref="Current"/>, lowest priority first, for <c>gui.StyleSheets</c>.</summary>
    public IReadOnlyList<StyleSheet> Sheets { get; private set; } = [];

    /// <summary>Incremented whenever <see cref="Sheets"/> changes.</summary>
    public int SheetsVersion { get; private set; }

    /// <inheritdoc />
    public string CommittedColorTheme => committed;

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
    public void ApplyColorTheme(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var resolved = Catalog.Find(id)?.Id ?? id;
        if (string.Equals(committed, resolved, StringComparison.Ordinal) && preview is null) return;

        committed = resolved;
        preview = null;
        Publish();
    }

    /// <inheritdoc />
    public void PreviewColorTheme(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        previewRenewed = true;

        if (Catalog.Find(id) is not { } theme || string.Equals(preview, theme.Id, StringComparison.Ordinal)) return;

        preview = theme.Id;
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

        if (previewRenewed)
        {
            previewRenewed = false;
            return;
        }

        if (preview is null) return;

        preview = null;
        Publish();
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
        var shown = Catalog.Find(preview ?? committed) ?? Catalog.Find(ThemeCatalog.DefaultColorTheme);
        var result = shown is null ? null : Compile(shown);
        if (result is null && Sheets.Count == 0 && shown?.Id != ThemeCatalog.DefaultColorTheme
            && Catalog.Find(ThemeCatalog.DefaultColorTheme) is { } fallback)
            result = Compile(fallback);

        if (result is not null && !ReferenceEquals(result.Sheets, Sheets))
        {
            Sheets = result.Sheets;
            SheetsVersion++;
        }

        var tokens = result?.Tokens ?? Current;
        Current = tokens with { TextScale = textSize / tokens.FontSize, Zoom = zoom };
        ThemeTokens.Current = Current;
        Changed?.Invoke();
    }

    /// <summary>Compiles a theme, or returns <c>null</c> and reports the located error.</summary>
    Compiled? Compile(ThemeInfo info)
    {
        if (compiled.TryGetValue(info.Id, out var cached)) return cached;

        try
        {
            var sheets = new StyleSheetCollection();
            if (contributions.Count > 0) sheets.Add(Catalog.Parse(Declarations(contributions), "plugin tokens"));
            sheets.Add(Catalog.Load(info.Id));
            if (userTokens.Count > 0) sheets.Add(Catalog.Parse(Declarations(userTokens), "user settings"));
            if (File.Exists(UserSheetPath))
                sheets.Add(Catalog.Parse(File.ReadAllText(UserSheetPath), UserSheetPath, new Uri(UserSheetPath)));

            var problems = new List<string>();
            var tokens = ThemeCompiler.Compile(sheets, info, problems);
            foreach (var problem in problems) log.LogDebug("Theme {Theme}: {Problem}", info.Id, problem);

            var result = new Compiled(tokens, [.. sheets]);
            compiled[info.Id] = result;
            Report(null);
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
        log.LogError("Theme error, keeping the last valid theme: {Message}", diagnostic.Message);
    }

    static string Declarations(IReadOnlyDictionary<string, string> tokens) =>
        string.Join('\n', tokens.Select(token => string.Create(CultureInfo.InvariantCulture,
            $"${token.Key} = {token.Value};")));

    static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex TokenName();
}
