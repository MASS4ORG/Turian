namespace Gaya.Packages;

/// <summary>Where a package can be installed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageScope>))]
public enum PackageScope
{
    /// <summary>Into one project, declared in its <c>Packages/manifest.json</c>.</summary>
    [JsonStringEnumMemberName("project")] Project,

    /// <summary>Into the application itself (themes, AI providers, tools), shared by every project.</summary>
    [JsonStringEnumMemberName("studio")] Studio,

    /// <summary>Into an installed game that hosts user content, loaded while it runs.</summary>
    [JsonStringEnumMemberName("game")] Game,
}

/// <summary>A sample a package offers to copy into the project on request.</summary>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Path">The sample's folder, relative to the package root (usually under <c>Samples~</c>).</param>
/// <param name="Description">What the sample shows.</param>
public sealed record PackageSample(string DisplayName, string Path, string? Description = null);

/// <summary>
/// A package's <c>package.json</c>: its identity, what it depends on, and how hosts treat it. Field names follow
/// Unity's package manifest where the meaning is the same.
/// </summary>
public sealed class PackageManifest
{
    /// <summary>The file name at a package's root.</summary>
    public const string FileName = "package.json";

    /// <summary>The package id, reverse-DNS (<c>com.example.inventory</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The package version.</summary>
    public SemanticVersion? Version { get; set; }

    /// <summary>The name shown to users.</summary>
    public string? DisplayName { get; set; }

    /// <summary>What the package is for.</summary>
    public string? Description { get; set; }

    /// <summary>The package's author.</summary>
    public string? Author { get; set; }

    /// <summary>The license, as an SPDX expression.</summary>
    public string? License { get; set; }

    /// <summary>Search keywords.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// The packages this one needs: id → a version range the project must satisfy, or a source
    /// (<c>git+https://…#v1.0.0</c>, <c>file:../other</c>) to fetch it from when the project does not declare it.
    /// </summary>
    public Dictionary<string, string> Dependencies { get; set; } = [];

    /// <summary>Packages needed only while developing this package itself, never by its consumers.</summary>
    public Dictionary<string, string> DevDependencies { get; set; } = [];

    /// <summary>
    /// NuGet packages the package's prebuilt assemblies need: id → exact version. Hosts that compile against the
    /// package add them to every project that uses it.
    /// </summary>
    public Dictionary<string, string> Nuget { get; set; } = [];

    /// <summary>Host → the host versions the package works with (<c>"my-app": "&gt;=1.2 &lt;2"</c>).</summary>
    public Dictionary<string, VersionRange> Engines { get; set; } = [];

    /// <summary>Where the package can be installed; the project scope when empty.</summary>
    public List<PackageScope> Scopes { get; set; } = [];

    /// <summary>Only the editor uses the package: nothing of it ships, though builds still need it.</summary>
    public bool EditorOnly { get; set; }

    /// <summary>Only the compiler uses the package (analyzers, source generators): never loaded nor shipped.</summary>
    public bool CompileTimeOnly { get; set; }

    /// <summary>
    /// What the package is, for discovery: <c>prefix:name</c> strings, where the prefix is a package id this one
    /// is or depends on, or a host's reserved prefix. Hosts act on the ones they know and keep the rest.
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Samples the user can copy into the project.</summary>
    public List<PackageSample> Samples { get; set; } = [];

    /// <summary>The scopes the package supports, defaulting to the project.</summary>
    [JsonIgnore]
    public IReadOnlyList<PackageScope> EffectiveScopes => Scopes.Count > 0 ? Scopes : [PackageScope.Project];

    /// <summary>Reads the <c>package.json</c> in <paramref name="packageRoot"/> and checks it.</summary>
    /// <param name="packageRoot">The package folder.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes any package may use, such as a host's name.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="PackageException">The file is missing, unreadable or invalid.</exception>
    public static PackageManifest Load(string packageRoot, IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        var path = Path.Combine(packageRoot, FileName);
        if (!File.Exists(path)) throw new PackageException($"{packageRoot} has no {FileName}.");

        PackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), PackageJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid package manifest: {ex.Message}", ex);
        }

        if (manifest is null) throw new PackageException($"{path} is empty.");
        manifest.Validate(path, reservedCategoryPrefixes ?? []);
        return manifest;
    }

    /// <summary>Writes the manifest into <paramref name="packageRoot"/>.</summary>
    /// <param name="packageRoot">The package folder.</param>
    public void Save(string packageRoot) =>
        File.WriteAllText(Path.Combine(packageRoot, FileName), JsonSerializer.Serialize(this, PackageJson.Options));

    void Validate(string path, IReadOnlyCollection<string> reservedCategoryPrefixes)
    {
        if (!PackageId.IsValid(Name)) throw new PackageException($"{path}: '{Name}' is not a valid package id.");
        if (Version is null) throw new PackageException($"{path}: package '{Name}' has no version.");

        foreach (var id in Dependencies.Keys.Concat(DevDependencies.Keys).Where(static id => !PackageId.IsValid(id)))
            throw new PackageException($"{path}: dependency '{id}' is not a valid package id.");

        if (EditorOnly && CompileTimeOnly)
            throw new PackageException($"{path}: a package cannot be both editor-only and compile-time-only.");

        foreach (var category in Categories)
        {
            var colon = category.IndexOf(':');
            var prefix = colon > 0 ? category[..colon] : string.Empty;
            if (colon <= 0 || colon == category.Length - 1)
                throw new PackageException($"{path}: category '{category}' must read 'prefix:name'.");
            if (prefix != Name && !Dependencies.ContainsKey(prefix) && !reservedCategoryPrefixes.Contains(prefix))
                throw new PackageException(
                    $"{path}: category '{category}' uses prefix '{prefix}', which is neither this package, one of its dependencies nor a reserved prefix.");
        }
    }
}

/// <summary>Rules for package ids.</summary>
public static class PackageId
{
    /// <summary>
    /// Whether <paramref name="id"/> is a package id: lowercase reverse-DNS, at least two dot-separated segments of
    /// letters, digits, <c>-</c> and <c>_</c>.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <returns>True for a valid id.</returns>
    public static bool IsValid([NotNullWhen(true)] string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 214 && id.Split('.') is { Length: >= 2 } segments
        && segments.All(static s => s.Length > 0 && char.IsAsciiLetterLower(s[0])
                                    && s.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'));
}

/// <summary>The JSON conventions of every package file: camelCase, indented, nulls left out.</summary>
public static class PackageJson
{
    /// <summary>The serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Package files are read by people: keep '+' in git+ sources and non-ASCII names as written.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
