using System.Text.RegularExpressions;

namespace Gaya.Host;

/// <summary>
/// Finds the theme sheets the studio can use: the host's embedded built-ins, the user's themes folder
/// (<c>~/.gaya/themes/**/*.pss</c>), studio-brick folders and sheets registered from code, in that order. A sheet's
/// <c>@const theme-id</c>, <c>icon-theme-id</c> or <c>font-pack-id</c> makes it selectable; a sheet with none is
/// only importable, by its file name without extension. A later source replaces an earlier sheet with the same id.
/// </summary>
public sealed partial class ThemeCatalog
{
    /// <summary>The embedded sheet that defines every token, imported by every built-in theme.</summary>
    public const string BaseId = "gaya.base";

    /// <summary>The id of the default color theme.</summary>
    public const string DefaultColorTheme = "gaya.dark";

    /// <summary>The import id of Guinevere's own default base sheet, which a look can start from.</summary>
    public const string GuinevereDefaultId = "guinevere.default";

    /// <summary>The id of the default look, which leaves Guinevere's own base sheet in place.</summary>
    public const string DefaultLook = "gaya.material";

    /// <summary>The id of the default icon theme.</summary>
    public const string DefaultIconTheme = "gaya.icons.color";

    /// <summary>The icon theme under every other one, which supplies the ids a theme leaves out.</summary>
    public const string FallbackIconTheme = "gaya.icons.mono";

    const string ResourcePrefix = "Gaya.Host.Themes.";
    const string Extension = ".pss";

    /// <summary>The built-in sheets in menu order.</summary>
    static readonly string[] BuiltInOrder =
    [
        "gaya.material", "gaya.icons.color", "gaya.icons.mono",
        "gaya.dark", "gaya.light", "gaya.dark-contrast", "gaya.dracula", "gaya.monokai", "gaya.one-dark", "gaya.nord",
        "gaya.gruvbox", "gaya.tokyo-night", "gaya.catppuccin", "gaya.solarized-dark", "gaya.solarized-light",
        "gaya.ayu-dark", "gaya.ayu-light",
    ];

    static readonly (string Constant, string Category)[] IdConstants =
    [
        ("look-id", ThemeCategories.Look),
        ("theme-id", ThemeCategories.ColorTheme),
        ("icon-theme-id", ThemeCategories.IconTheme),
        ("font-pack-id", ThemeCategories.FontPack),
    ];

    readonly ILogger log;
    readonly string cacheFolder;
    Uri? embeddedBase;
    bool embeddedExtracted;
    readonly Dictionary<string, ThemeSource> registered = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> registeredOrder = [];
    IReadOnlyList<string> brickFolders = [];
    Dictionary<string, Entry> byImportId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A scanned sheet: its metadata when selectable, and the text it was scanned with.</summary>
    sealed record Entry(ThemeInfo? Info, string ImportId, string SourceName, string Text, Uri? BaseUri);

    /// <summary>Scans the built-ins and the user's themes folder.</summary>
    /// <param name="log">Receives unreadable sheets and replaced ids.</param>
    /// <param name="userFolder">The user's themes folder; <c>~/.gaya/themes</c> when null.</param>
    /// <param name="cacheFolder">Where the built-in icon files are extracted; <c>~/.gaya/cache</c> when null.</param>
    public ThemeCatalog(ILogger? log = null, string? userFolder = null, string? cacheFolder = null)
    {
        this.log = log ?? NullLogger.Instance;
        UserFolder = userFolder ?? UserConfigPath.For("themes");
        this.cacheFolder = cacheFolder ?? UserConfigPath.For("cache");
        Refresh();
    }

    /// <summary>The folder scanned for the user's own <c>.pss</c> sheets.</summary>
    public string UserFolder { get; }

    /// <summary>Incremented by every rescan and registration.</summary>
    public int Version { get; private set; }

    /// <summary>Every selectable sheet, built-in color themes first in menu order.</summary>
    public IReadOnlyList<ThemeInfo> All { get; private set; } = [];

    /// <summary>The selectable color themes.</summary>
    public IReadOnlyList<ThemeInfo> ColorThemes { get; private set; } = [];

    /// <summary>The selectable looks.</summary>
    public IReadOnlyList<ThemeInfo> Looks { get; private set; } = [];

    /// <summary>The selectable icon themes.</summary>
    public IReadOnlyList<ThemeInfo> IconThemes { get; private set; } = [];

    /// <summary>The folders scanned for brick sheets, each a brick's <c>Themes</c> folder.</summary>
    public IReadOnlyList<string> BrickFolders => brickFolders;

    /// <summary>Finds a selectable sheet by id, or a color theme by its display name.</summary>
    /// <param name="idOrName">The id, or the name settings stored before themes had ids.</param>
    /// <returns>The sheet's metadata, or <c>null</c>.</returns>
    public ThemeInfo? Find(string idOrName)
    {
        ArgumentNullException.ThrowIfNull(idOrName);
        return All.FirstOrDefault(info => Same(info.Id, idOrName))
               ?? ColorThemes.FirstOrDefault(info => Same(info.Name, idOrName));
    }

    /// <summary>Adds or replaces a sheet supplied from code.</summary>
    /// <param name="source">The sheet; it must declare a theme, icon-theme or font-pack id.</param>
    /// <returns>The sheet's metadata.</returns>
    /// <exception cref="ArgumentException">The sheet declares no id.</exception>
    public ThemeInfo Register(ThemeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (info, _) = Describe(source.Text, ThemeOrigin.Code, null);
        if (info is null) throw new ArgumentException("A theme sheet needs @const theme-id.", nameof(source));

        if (registered.TryAdd(info.Id, source)) registeredOrder.Add(info.Id);
        else registered[info.Id] = source;
        Refresh();
        return Find(info.Id)!;
    }

    /// <summary>Sets the brick <c>Themes</c> folders to scan and rescans.</summary>
    /// <param name="folders">Absolute folders; missing ones are skipped.</param>
    public void SetBrickFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        brickFolders = [.. folders];
        Refresh();
    }

    /// <summary>Rescans every source; sheet text is read once per scan.</summary>
    public void Refresh()
    {
        var scanned = new List<Entry>();
        var index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        if (!embeddedExtracted)
        {
            embeddedExtracted = true;
            embeddedBase = EmbeddedIcons.Extract(cacheFolder, log);
        }
        foreach (var name in EmbeddedResources())
            Add(scanned, index, ThemeOrigin.BuiltIn, name[ResourcePrefix.Length..^Extension.Length],
                name[ResourcePrefix.Length..], null, () => ReadResource(name), embeddedBase);
        foreach (var path in SheetFiles(UserFolder))
            Add(scanned, index, ThemeOrigin.User, Path.GetFileNameWithoutExtension(path), path, path,
                () => File.ReadAllText(path));
        foreach (var path in brickFolders.SelectMany(SheetFiles))
            Add(scanned, index, ThemeOrigin.Brick, Path.GetFileNameWithoutExtension(path), path, path,
                () => File.ReadAllText(path));
        foreach (var id in registeredOrder)
            Add(scanned, index, ThemeOrigin.Code, id, registered[id].SourceName ?? id, null,
                () => registered[id].Text);

        byImportId = index;
        All = [.. scanned.Where(entry => entry.Info is not null).Select(entry => entry.Info!)];
        ColorThemes = [.. All.Where(info => info.Category == ThemeCategories.ColorTheme)];
        Looks = [.. All.Where(info => info.Category == ThemeCategories.Look)];
        IconThemes = [.. All.Where(info => info.Category == ThemeCategories.IconTheme)];
        Version++;
    }

    /// <summary>A cheap fingerprint of the user and brick sheet files, which changes when one is added, edited or removed.</summary>
    /// <returns>The fingerprint.</returns>
    public long Fingerprint()
    {
        var hash = new HashCode();
        foreach (var path in SheetFiles(UserFolder).Concat(brickFolders.SelectMany(SheetFiles)))
        {
            var file = new FileInfo(path);
            hash.Add(path);
            if (!file.Exists) continue;
            hash.Add(file.LastWriteTimeUtc.Ticks);
            hash.Add(file.Length);
        }
        return hash.ToHashCode();
    }

    /// <summary>Parses a selectable or importable sheet with the catalog resolving its <c>@import</c>s.</summary>
    /// <param name="id">The sheet's id or import id.</param>
    /// <returns>The parsed sheet.</returns>
    /// <exception cref="KeyNotFoundException">No sheet has that id.</exception>
    /// <exception cref="StyleSheetException">The sheet or one of its imports is malformed.</exception>
    public StyleSheet Load(string id)
    {
        var importId = Find(id)?.Id ?? id;
        if (!byImportId.TryGetValue(importId, out var entry))
            throw new KeyNotFoundException($"No theme sheet has the id '{id}'.");
        return Parse(entry.Text, entry.SourceName, entry.BaseUri);
    }

    /// <summary>Parses sheet text with the catalog resolving its <c>@import</c>s.</summary>
    /// <param name="text">The <c>.pss</c> source.</param>
    /// <param name="sourceName">Name reported in errors.</param>
    /// <param name="baseUri">Base for relative <c>url()</c> values.</param>
    /// <returns>The parsed sheet.</returns>
    /// <exception cref="StyleSheetException">The text or one of its imports is malformed.</exception>
    public StyleSheet Parse(string text, string sourceName, Uri? baseUri = null) =>
        StyleSheet.Parse(text, new StyleSheetOptions
        {
            SourceName = sourceName,
            BaseUri = baseUri,
            ImportResolver = Resolve,
        });

    /// <summary>The metadata a sheet declares about itself, ignoring what it imports.</summary>
    /// <param name="text">The <c>.pss</c> source.</param>
    /// <param name="origin">Where the sheet was found.</param>
    /// <param name="path">The sheet's file, if any.</param>
    /// <returns>The metadata when the sheet declares an id, and its own constants.</returns>
    internal static (ThemeInfo? Info, IReadOnlyDictionary<string, string> Constants) Describe(string text,
        ThemeOrigin origin, string? path)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ConstPattern().Matches(text))
            constants[match.Groups[1].Value] = StyleValue.Unquote(match.Groups[2].Value);

        foreach (var (constant, category) in IdConstants)
        {
            if (!constants.TryGetValue(constant, out var id) || id.Length == 0) continue;
            var prefix = constant[..^"id".Length];
            var name = constants.GetValueOrDefault(prefix + "name") ?? id;
            var info = new ThemeInfo(id, name, Kind(constants.GetValueOrDefault(prefix + "kind")), category, origin,
                path, constants.GetValueOrDefault(prefix + "theme-dark"),
                constants.GetValueOrDefault(prefix + "theme-light"));
            return (info, constants);
        }
        return (null, constants);
    }

    /// <summary>Parses a <c>theme-kind</c> value; anything unrecognized is dark.</summary>
    static ThemeKind Kind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeKind.Light,
        "high-contrast-dark" => ThemeKind.HighContrastDark,
        "high-contrast-light" => ThemeKind.HighContrastLight,
        _ => ThemeKind.Dark,
    };

    StyleSheetText? Resolve(string id)
    {
        if (byImportId.TryGetValue(id, out var entry)) return new StyleSheetText(entry.Text, entry.SourceName, entry.BaseUri);
        return id == GuinevereDefaultId ? new StyleSheetText(ExcaliburStyles.DefaultSheetText, id + Extension, null) : null;
    }

    void Add(List<Entry> scanned, Dictionary<string, Entry> index, ThemeOrigin origin, string stem, string sourceName,
        string? path, Func<string> read, Uri? baseUri = null)
    {
        string text;
        try
        {
            text = read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Theme sheet {Path} could not be read", sourceName);
            return;
        }

        var (info, _) = Describe(text, origin, path);
        var entry = new Entry(info, info?.Id ?? stem, sourceName,
            text, path is null ? baseUri : new Uri(Path.GetFullPath(path)));

        if (index.TryGetValue(entry.ImportId, out var replaced))
        {
            log.LogInformation("Theme {Id} from {Source} replaces the one from {Previous}", entry.ImportId, sourceName,
                replaced.SourceName);
            scanned[scanned.IndexOf(replaced)] = entry;
        }
        else
        {
            scanned.Add(entry);
        }
        index[entry.ImportId] = entry;
    }

    static IEnumerable<string> EmbeddedResources()
    {
        var names = typeof(ThemeCatalog).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                           && name.EndsWith(Extension, StringComparison.Ordinal))
            .ToList();
        int Rank(string name)
        {
            var index = Array.IndexOf(BuiltInOrder, name[ResourcePrefix.Length..^Extension.Length]);
            return index < 0 ? BuiltInOrder.Length : index;
        }
        return names.OrderBy(Rank).ThenBy(name => name, StringComparer.Ordinal);
    }

    static string ReadResource(string name)
    {
        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static IEnumerable<string> SheetFiles(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        : [];

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[ \t]*@const\s+([A-Za-z_][A-Za-z0-9_-]*)\s*=\s*([^;]+);", RegexOptions.Multiline)]
    private static partial Regex ConstPattern();
}
