namespace Gaya.Host;

/// <summary>How serious a <see cref="ThemeFinding"/> is; only errors fail a verification.</summary>
public enum ThemeFindingSeverity
{
    /// <summary>The sheet cannot be used.</summary>
    Error,

    /// <summary>The sheet works but something looks wrong, such as low contrast.</summary>
    Warning,

    /// <summary>Worth knowing, such as a token <c>gaya.base</c> does not define.</summary>
    Note,
}

/// <summary>One problem a theme sheet has.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Source">The sheet's file.</param>
/// <param name="Line">1-based line, or 0 when unknown.</param>
/// <param name="Column">1-based column, or 0 when unknown.</param>
public sealed record ThemeFinding(ThemeFindingSeverity Severity, string Message, string Source, int Line = 0,
    int Column = 0)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{Source}{(Line > 0 ? $":{Line}:{Column}" : "")}: {Severity.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>
/// Checks theme sheets before they are installed: they must parse and resolve, every token they use must exist and
/// have the right type, and text should contrast with the surfaces it is drawn on.
/// </summary>
public static class ThemeVerifier
{
    /// <summary>The minimum contrast ratio of <c>$ink</c> on the panel, field and chrome surfaces.</summary>
    public const double InkContrast = 4.5;

    /// <summary>The minimum contrast ratio of <c>$ink-dim</c> on the panel surface.</summary>
    public const double InkDimContrast = 3.0;

    static readonly (string Ink, string Surface, double Minimum)[] ContrastPairs =
    [
        ("ink", "surface-panel", InkContrast),
        ("ink", "surface-field", InkContrast),
        ("ink", "surface-chrome", InkContrast),
        ("ink-dim", "surface-panel", InkDimContrast),
    ];

    /// <summary>Verifies a <c>.pss</c> file, a folder of them, or a brick folder (its <c>Themes</c> folder).</summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>Every finding, errors first.</returns>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    public static IReadOnlyList<ThemeFinding> Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var folder = Directory.Exists(full)
            ? File.Exists(Path.Combine(full, "package.json")) ? Path.Combine(full, "Themes") : full
            : File.Exists(full) ? Path.GetDirectoryName(full)! : throw new FileNotFoundException("No theme sheet here.", full);
        IEnumerable<string> files = File.Exists(full)
            ? [full]
            : Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.pss", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                : [];

        var catalog = new ThemeCatalog(NullLogger.Instance, Path.Combine(folder, ".no-user-themes"));
        catalog.SetBrickFolders([folder]);
        var known = catalog.Load(ThemeCatalog.BaseId).Variables.Keys.ToHashSet(StringComparer.Ordinal);

        var findings = new List<ThemeFinding>();
        foreach (var file in files) findings.AddRange(VerifyFile(catalog, file, known));
        return [.. findings.OrderBy(finding => finding.Severity)];
    }

    static IEnumerable<ThemeFinding> VerifyFile(ThemeCatalog catalog, string file, IReadOnlySet<string> known)
    {
        var text = File.ReadAllText(file);
        var (info, _) = ThemeCatalog.Describe(text, ThemeOrigin.Brick, file);
        var findings = new List<ThemeFinding>();
        try
        {
            var sheet = catalog.Parse(text, file, new Uri(file));
            if (info is not { Category: ThemeCategories.ColorTheme }) return findings;

            var problems = new List<string>();
            var tokens = ThemeCompiler.Compile([sheet], info, problems);
            findings.AddRange(problems.Select(problem => new ThemeFinding(ThemeFindingSeverity.Error, problem, file)));
            findings.AddRange(tokens.Values.Where(token => token.Value.Contains('$'))
                .Select(token => new ThemeFinding(ThemeFindingSeverity.Error,
                    $"${token.Key} uses an unknown token: {token.Value}", file)));
            findings.AddRange(OwnTokens(text, file).Where(name => !known.Contains(name))
                .Select(name => new ThemeFinding(ThemeFindingSeverity.Note,
                    $"${name[2..]} is not a gaya.base token; only a plugin that declares it reads it", file)));
            findings.AddRange(Contrast(tokens, file));
        }
        catch (StyleSheetException ex)
        {
            findings.Add(new ThemeFinding(ThemeFindingSeverity.Error, ex.Reason, ex.SourceName ?? file, ex.Line, ex.Column));
        }
        return findings;
    }

    /// <summary>The tokens the sheet itself declares, without what it imports.</summary>
    static IEnumerable<string> OwnTokens(string text, string file) =>
        StyleSheet.Parse(text, new StyleSheetOptions
        {
            SourceName = file,
            ImportResolver = _ => new StyleSheetText(""),
        }).Variables.Keys;

    static IEnumerable<ThemeFinding> Contrast(ThemeTokens tokens, string file)
    {
        foreach (var (ink, surface, minimum) in ContrastPairs)
        {
            if (!tokens.Colors.TryGetValue(ink, out var front) || !tokens.Colors.TryGetValue(surface, out var back))
                continue;
            var ratio = ContrastRatio(front, back);
            if (ratio < minimum)
                yield return new ThemeFinding(ThemeFindingSeverity.Warning,
                    $"${ink} on ${surface} has contrast {ratio:0.00}:1, below {minimum:0.0}:1", file);
        }
    }

    /// <summary>The WCAG contrast ratio of two opaque colors, from 1 to 21.</summary>
    /// <param name="a">One color.</param>
    /// <param name="b">The other color.</param>
    /// <returns>The ratio.</returns>
    public static double ContrastRatio(Color a, Color b)
    {
        var (light, dark) = (Luminance(a), Luminance(b));
        if (light < dark) (light, dark) = (dark, light);
        return (light + 0.05) / (dark + 0.05);
    }

    static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    static double Linear(byte channel)
    {
        var c = channel / 255d;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
