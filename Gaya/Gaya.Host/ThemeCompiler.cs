namespace Gaya.Host;

/// <summary>
/// Turns a theme's layered sheets into the typed <see cref="ThemeTokens"/> snapshot panels read every frame. Every
/// token is evaluated once here, so drawing never resolves a sheet.
/// </summary>
public static class ThemeCompiler
{
    /// <summary>Evaluates every token of <paramref name="sheets"/> into a snapshot.</summary>
    /// <param name="sheets">The layered sheets, lowest priority first.</param>
    /// <param name="info">The theme's metadata.</param>
    /// <param name="problems">Receives tokens the typed snapshot needs that are missing or of the wrong type.</param>
    /// <returns>The snapshot, with <see cref="ThemeTokens.Default"/> values where a token is unusable.</returns>
    public static ThemeTokens Compile(StyleSheetCollection sheets, ThemeInfo info, ICollection<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(info);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        var lengths = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var key in TokenKeys(sheets))
        {
            if (sheets.GetToken(key) is not { } value) continue;
            var name = key[2..];
            values[name] = value;
            if (StyleValue.TryColor(value, out var color)) colors[name] = color;
            else if (StyleValue.TryLength(value, out var length, out var percent) && !percent) lengths[name] = length;
        }

        var reader = new Reader(colors, lengths, problems);
        var d = ThemeTokens.Default;
        return new ThemeTokens
        {
            Id = info.Id,
            Name = info.Name,
            Kind = info.Kind,
            Background = reader.Color("surface-window", d.Background),
            Panel = reader.Color("surface-panel", d.Panel),
            Chrome = reader.Color("surface-chrome", d.Chrome),
            Field = reader.Color("surface-field", d.Field),
            Hover = reader.Color("surface-hover", d.Hover),
            SurfaceActive = reader.Color("surface-active", d.SurfaceActive),
            Popup = reader.Color("surface-popup", d.Popup),
            EditorArea = reader.Color("surface-editor-area", d.EditorArea),
            Scrim = reader.Color("scrim", d.Scrim),
            Shadow = reader.Color("shadow", d.Shadow),
            Border = reader.Color("border", d.Border),
            BorderStrong = reader.Color("border-strong", d.BorderStrong),
            Divider = reader.Color("divider", d.Divider),
            FocusRing = reader.Color("focus-ring", d.FocusRing),
            Ink = reader.Color("ink", d.Ink),
            InkDim = reader.Color("ink-dim", d.InkDim),
            InkFaint = reader.Color("ink-faint", d.InkFaint),
            InkOnAccent = reader.Color("ink-on-accent", d.InkOnAccent),
            Link = reader.Color("link", d.Link),
            Accent = reader.Color("accent", d.Accent),
            AccentHover = reader.Color("accent-hover", d.AccentHover),
            AccentFill = reader.Color("accent-fill", d.AccentFill),
            Selection = reader.Color("selection", d.Selection),
            SelectionInk = reader.Color("selection-ink", d.SelectionInk),
            Error = reader.Color("error", d.Error),
            Warning = reader.Color("warning", d.Warning),
            Success = reader.Color("success", d.Success),
            Info = reader.Color("info", d.Info),
            Hint = reader.Color("hint", d.Hint),
            Folder = reader.Color("folder", d.Folder),
            LogDebug = reader.Color("log-debug", d.LogDebug),
            LogInfo = reader.Color("log-info", d.LogInfo),
            LogWarning = reader.Color("log-warning", d.LogWarning),
            LogError = reader.Color("log-error", d.LogError),
            Ansi = [.. Enumerable.Range(0, 16).Select(i => reader.Color($"ansi-{i}", Color.Empty))],
            Statuses = Enum.GetValues<StatusKind>().ToDictionary(kind => kind, reader.Status),
            FontSize = reader.Length("font-size", d.FontSize),
            FontSizeSmall = reader.Length("font-size-small", d.FontSizeSmall),
            FontSizeLarge = reader.Length("font-size-large", d.FontSizeLarge),
            FontSizeTitle = reader.Length("font-size-title", d.FontSizeTitle),
            MenuHeight = reader.Length("menu-height", d.MenuHeight),
            StatusHeight = reader.Length("status-height", d.StatusHeight),
            HeaderHeight = reader.Length("header-height", d.HeaderHeight),
            RowHeight = reader.Length("row-height", d.RowHeight),
            Gap = reader.Length("gap", d.Gap),
            Padding = reader.Length("padding", d.Padding),
            Spacing = reader.Length("spacing", d.Spacing),
            Radius = reader.Length("radius", d.Radius),
            RadiusSmall = reader.Length("radius-small", d.RadiusSmall),
            BorderWidth = reader.Length("border-width", d.BorderWidth),
            FocusRingWidth = reader.Length("focus-ring-width", d.FocusRingWidth),
            Colors = colors,
            Lengths = lengths,
            Values = values,
        };
    }

    /// <summary>Every token key (<c>--name</c>) the sheets or the host overrides define.</summary>
    static IEnumerable<string> TokenKeys(StyleSheetCollection sheets) =>
        sheets.SelectMany(sheet => sheet.Variables.Keys).Concat(sheets.Tokens.Keys).Distinct(StringComparer.Ordinal);

    /// <summary>Reads typed tokens, reporting the ones that are missing or of the wrong type.</summary>
    sealed class Reader(Dictionary<string, Color> colors, Dictionary<string, float> lengths,
        ICollection<string>? problems)
    {
        public Color Color(string name, Color fallback)
        {
            if (colors.TryGetValue(name, out var color)) return color;
            problems?.Add($"${name} is not a color");
            return fallback;
        }

        public float Length(string name, float fallback)
        {
            if (lengths.TryGetValue(name, out var length)) return length;
            problems?.Add($"${name} is not a length");
            return fallback;
        }

        public StatusColors Status(StatusKind kind)
        {
            var name = kind.ToString().ToLowerInvariant();
            var fallback = ThemeTokens.Default.Status(kind);
            return new StatusColors(Color(name, fallback.Color), Color(name + "-bg", fallback.Background),
                Color(name + "-border", fallback.Border));
        }
    }
}
