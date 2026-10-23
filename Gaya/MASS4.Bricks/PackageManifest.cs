namespace MASS4.Bricks;

/// <summary>Where a package can be installed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageScope>))]
public enum PackageScope
{
    /// <summary>Into one project, declared in its <c>Bricks/manifest.json</c>.</summary>
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
/// What a packed brick's prebuilt payload (its <c>Precast~</c> folder) was built with, written into the
/// <c>package.json</c> inside the <c>.brick</c> file.
/// </summary>
public sealed class PackagePrecast
{
    /// <summary>Host → the exact host version the assemblies were compiled against; consumers on another major or minor version ignore the payload.</summary>
    public Dictionary<string, SemanticVersion> BuiltWith { get; set; } = [];

    /// <summary>Names of the assemblies games run with (<c>Precast~/lib</c>).</summary>
    public List<string> Assemblies { get; set; } = [];

    /// <summary>Names of the assemblies only the editor loads (<c>Precast~/editor</c>).</summary>
    public List<string> EditorAssemblies { get; set; } = [];
}

/// <summary>
/// Set in a bake twin: the source brick it is the consumable form of, pinned to the exact <c>.brick</c> file. A twin
/// installs in place of its source, so whatever depends on the source accepts the twin.
/// </summary>
public sealed class PackageBakeOf
{
    /// <summary>The source brick's id.</summary>
    public string Brick { get; set; } = string.Empty;

    /// <summary>The source brick's version.</summary>
    public SemanticVersion? Version { get; set; }

    /// <summary>The source <c>.brick</c> file's integrity, <c>sha256-</c> + base64.</summary>
    public string Integrity { get; set; } = string.Empty;
}

/// <summary>
/// A package's <c>package.json</c>: its identity, what it depends on, and how hosts treat it. Field names follow
/// Unity's package manifest where the meaning is the same.
/// </summary>
public sealed class PackageManifest
{
    /// <summary>The file name at a package's root.</summary>
    public const string FileName = "package.json";

    /// <summary>The key brick format v2 names the id with; earlier manifests use <c>name</c>.</summary>
    const string IdKey = "id";

    /// <summary>Whether the file names the id <c>id</c>, as brick format v2 does, rather than <c>name</c>.</summary>
    [JsonIgnore]
    public bool UsesId { get; set; }

    /// <summary>The package id, reverse-DNS (<c>com.example.inventory</c>).</summary>
    [SetOrder(-30), Tooltip("Permanent reverse-DNS brick identifier.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The package version.</summary>
    [SetOrder(-20)]
    public SemanticVersion? Version { get; set; }

    /// <summary>The name shown to users.</summary>
    [SetOrder(-10)]
    public string? DisplayName { get; set; }

    /// <summary>What the package is for.</summary>
    [TextArea(2, 8)]
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
    [Tooltip("Other bricks required by this brick.")]
    public Dictionary<string, string> Dependencies { get; set; } = [];

    /// <summary>Packages needed only while developing this package itself, never by its consumers.</summary>
    public Dictionary<string, string> DevDependencies { get; set; } = [];

    /// <summary>
    /// NuGet packages the package's prebuilt assemblies need: id → exact version. Hosts that compile against the
    /// package add them to every project that uses it.
    /// </summary>
    [Tooltip("NuGet libraries referenced by generated consumer projects.")]
    public Dictionary<string, string> Nuget { get; set; } = [];

    /// <summary>
    /// NuGet packages only one platform needs: platform id → (package id → exact version). They add to
    /// <see cref="Nuget"/> for builds of that platform.
    /// </summary>
    [Tooltip("NuGet libraries referenced only when building for the named platform.")]
    public Dictionary<string, Dictionary<string, string>> PlatformNuget { get; set; } = [];

    /// <summary>The platform ids the package supports, such as <c>desktop</c>; every platform when empty.</summary>
    [Tooltip("Build platforms the brick supports; empty means all.")]
    public List<string> Platforms { get; set; } = [];

    /// <summary>Host → the host versions the package works with (<c>"my-app": "&gt;=1.2 &lt;2"</c>).</summary>
    public Dictionary<string, VersionRange> Engines { get; set; } = [];

    /// <summary>Where the package can be installed; the project scope when empty.</summary>
    public List<PackageScope> Scopes { get; set; } = [];

    /// <summary>Only the editor uses the package: nothing of it ships, though builds still need it.</summary>
    public bool EditorOnly { get; set; }

    /// <summary>Only the compiler uses the package (analyzers, source generators): never loaded nor shipped.</summary>
    public bool CompileTimeOnly { get; set; }

    /// <summary>
    /// Only client builds ship the package, as a renderer or window does: server builds leave it out, together with
    /// every package depending on it.
    /// </summary>
    public bool ClientOnly { get; set; }

    /// <summary>
    /// What the package is, for discovery: <c>prefix:name</c> strings, where the prefix is a package id this one
    /// is or depends on, or a host's reserved prefix. Hosts act on the ones they know and keep the rest.
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// For an embedded fork: the version it was copied from, <c>id@version</c> plus the source's commit or hash in
    /// parentheses when it has one.
    /// </summary>
    public string? Upstream { get; set; }

    /// <summary>Set in a packed brick whose <c>Precast~</c> payload was built; absent in a source folder.</summary>
    public PackagePrecast? Precast { get; set; }

    /// <summary>Licensing and access terms: entitlement, license agreement, whether the brick may be forked or republished.</summary>
    public PackageStoreInfo? Store { get; set; }

    /// <summary>Samples the user can copy into the project.</summary>
    public List<PackageSample> Samples { get; set; } = [];

    /// <summary>
    /// Globs of files the <c>.brick</c> file leaves out, relative to the brick folder, such as <c>Tests~</c> or
    /// <c>**/*.pdb</c>. A glob without <c>/</c> matches a name at any depth; a matching folder drops everything in it.
    /// </summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Set in a bake twin: the source brick it was baked from.</summary>
    public PackageBakeOf? BakeOf { get; set; }

    /// <summary>
    /// Whether bake twins of the package may exist: <c>auto</c>, the default, lets consumers take one when it is
    /// published; <c>none</c> means the package itself is the consumable form and twins of it are refused.
    /// </summary>
    public string? Bake { get; set; }

    /// <summary>
    /// Fingerprints (<c>SHA256:…</c>) of keys, besides the package's own publisher, whose bake twins of it consumers
    /// accept.
    /// </summary>
    public List<string> TrustedBakers { get; set; } = [];

    /// <summary>
    /// Brick format v2: role → the path, or array of paths, under <c>Brick~/</c> holding it, such as <c>source</c>,
    /// <c>tests</c>, <c>precast</c>, <c>bake</c>, <c>samples</c> or <c>docs</c>. Hosts ignore roles they do not know.
    /// </summary>
    public Dictionary<string, JsonElement> Roles { get; set; } = [];

    /// <summary>The paths a role names, in order; none when the manifest does not declare it.</summary>
    /// <param name="role">The role.</param>
    /// <returns>Paths relative to the brick folder, with <c>/</c> separators.</returns>
    public IReadOnlyList<string> RolePaths(string role) =>
        Roles.TryGetValue(role, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => [.. value.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.String).Select(static e => e.GetString()!)],
            _ => [],
        } : [];

    /// <summary>Whether consumers may take a bake twin of the package in its place.</summary>
    [JsonIgnore]
    public bool AllowsTwins => Bake != BakeNone;

    /// <summary>The <see cref="Bake"/> value that refuses twins.</summary>
    public const string BakeNone = "none";

    /// <summary>Whether the package supports building for <paramref name="platform"/>.</summary>
    /// <param name="platform">A platform id, ignoring case.</param>
    /// <returns>True when the package lists no platforms or lists this one.</returns>
    public bool SupportsPlatform(string platform) =>
        Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

    /// <summary>The NuGet packages a build for <paramref name="platform"/> needs: the common ones plus that platform's.</summary>
    /// <param name="platform">A platform id, ignoring case.</param>
    /// <returns>Package id → exact version.</returns>
    public IReadOnlyDictionary<string, string> NugetFor(string platform)
    {
        var merged = new Dictionary<string, string>(Nuget);
        foreach (var (_, extra) in PlatformNuget.Where(p => string.Equals(p.Key, platform, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (package, version) in extra)
            {
                merged[package] = version;
            }
        }

        return merged;
    }

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
        if (!File.Exists(path))
        {
            throw new PackageException($"{packageRoot} has no {FileName}.");
        }

        return Parse(File.ReadAllText(path), path, reservedCategoryPrefixes);
    }

    /// <summary>Reads and checks manifest text.</summary>
    /// <param name="json">The <c>package.json</c> text.</param>
    /// <param name="path">Where the text came from, for error messages.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes any package may use, such as a host's name.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="PackageException">The text is invalid.</exception>
    public static PackageManifest Parse(string json, string path, IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        PackageManifest? manifest;
        try
        {
            // Brick format v2 names the id "id"; it is read as "name" and written back as it was found.
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is JsonObject identity && identity.ContainsKey(IdKey) && identity.ContainsKey("name"))
            {
                throw new PackageException($"{path}: use either id or name, not both.");
            }
            var usesId = node is JsonObject root && root.ContainsKey(IdKey) && !root.ContainsKey("name");
            if (usesId)
            {
                var obj = node!.AsObject();
                var id = obj[IdKey];
                obj.Remove(IdKey);
                obj["name"] = id;
            }

            manifest = node.Deserialize<PackageManifest>(PackageJson.Options);
            manifest?.UsesId = usesId;
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid package manifest: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new PackageException($"{path} is empty.");
        }

        manifest.Validate(path, reservedCategoryPrefixes ?? []);
        return manifest;
    }

    /// <summary>Writes the manifest into <paramref name="packageRoot"/>.</summary>
    /// <param name="packageRoot">The package folder.</param>
    public void Save(string packageRoot) => File.WriteAllText(Path.Combine(packageRoot, FileName), ToJson());

    /// <summary>The manifest's text. Empty lists and false flags are the defaults, so they are left out to keep a hand-edited file readable.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson()
    {
        var node = JsonSerializer.SerializeToNode(this, PackageJson.Options)!.AsObject();
        foreach (var name in node.Where(static p => IsDefault(p.Value)).Select(static p => p.Key).ToList())
        {
            node.Remove(name);
        }

        if (!UsesId)
        {
            return node.ToJsonString(PackageJson.Options);
        }

        // Keep the id first, under the name the file used.
        var ordered = new JsonObject { [IdKey] = node["name"]?.DeepClone() };
        foreach (var (key, value) in node.Where(static p => p.Key != "name").ToList())
        {
            ordered[key] = value?.DeepClone();
        }

        return ordered.ToJsonString(PackageJson.Options);
    }

    static bool IsDefault(JsonNode? value) =>
        value is JsonArray { Count: 0 } or JsonObject { Count: 0 }
        || value is JsonValue json && json.TryGetValue<bool>(out var flag) && !flag;

    void Validate(string path, IReadOnlyCollection<string> reservedCategoryPrefixes)
    {
        if (!PackageId.IsValid(Name))
        {
            throw new PackageException($"{path}: '{Name}' is not a valid package id.");
        }

        if (Version is null)
        {
            throw new PackageException($"{path}: package '{Name}' has no version.");
        }

        foreach (var id in Dependencies.Keys.Concat(DevDependencies.Keys).Where(static id => !PackageId.IsValid(id)))
        {
            throw new PackageException($"{path}: dependency '{id}' is not a valid package id.");
        }

        ValidateShipping(path);
        ValidateExclude(path);
        ValidateBakeOf(path);
        ValidateBake(path);
        ValidateRoles(path);

        if (Platforms.Concat(PlatformNuget.Keys).Any(static p => string.IsNullOrWhiteSpace(p)))
        {
            throw new PackageException($"{path}: platform ids cannot be empty.");
        }

        foreach (var (package, version) in Nuget.Concat(PlatformNuget.Values.SelectMany(static extra => extra)))
        {
            if (version.Contains('*'))
            {
                throw new PackageException($"{path}: NuGet package '{package}' version '{version}' is a floating version; pin an exact version.");
            }
        }

        foreach (var category in Categories)
        {
            var colon = category.IndexOf(':');
            var prefix = colon > 0 ? category[..colon] : string.Empty;
            if (colon <= 0 || colon == category.Length - 1)
            {
                throw new PackageException($"{path}: category '{category}' must read 'prefix:name'.");
            }

            if (prefix != Name && prefix != BakeOf?.Brick && !Dependencies.ContainsKey(prefix) && !reservedCategoryPrefixes.Contains(prefix))
            {
                throw new PackageException(
                    $"{path}: category '{category}' uses prefix '{prefix}', which is neither this package, one of its dependencies nor a reserved prefix.");
            }
        }
    }

    void ValidateExclude(string path)
    {
        foreach (var glob in Exclude)
        {
            if (!BrickGlob.IsValid(glob))
            {
                throw new PackageException($"{path}: exclude '{glob}' must be a relative path glob using '/', without '..'.");
            }

            if (BrickGlob.Matches(glob, FileName))
            {
                throw new PackageException($"{path}: exclude '{glob}' would leave out {FileName}.");
            }
        }
    }

    void ValidateBakeOf(string path)
    {
        if (BakeOf is not { } bakeOf)
        {
            return;
        }

        if (!PackageId.IsValid(bakeOf.Brick) || bakeOf.Brick == Name)
        {
            throw new PackageException($"{path}: bakeOf.brick '{bakeOf.Brick}' must be the id of another brick.");
        }

        if (bakeOf.Version is null)
        {
            throw new PackageException($"{path}: bakeOf has no version.");
        }

        if (!bakeOf.Integrity.StartsWith("sha256-", StringComparison.Ordinal))
        {
            throw new PackageException($"{path}: bakeOf.integrity must be the source file's sha256- hash.");
        }
    }

    void ValidateRoles(string path)
    {
        foreach (var (role, value) in Roles)
        {
            if (value.ValueKind is not JsonValueKind.String and not JsonValueKind.Array
                || value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(static e => e.ValueKind != JsonValueKind.String))
            {
                throw new PackageException($"{path}: role '{role}' must be a path or an array of paths.");
            }

            if (RolePaths(role).FirstOrDefault(static p => !BrickLayout.IsInsideReservedRoot(p)) is { } outside)
            {
                throw new PackageException($"{path}: role '{role}' path '{outside}' must lie inside {BrickLayout.ReservedRoot}/, using '/', without '..'.");
            }
        }
    }

    void ValidateBake(string path)
    {
        if (Bake is not null and not "auto" and not BakeNone)
        {
            throw new PackageException($"{path}: bake must be 'auto' or '{BakeNone}', not '{Bake}'.");
        }

        if (TrustedBakers.FirstOrDefault(static key => !key.StartsWith("SHA256:", StringComparison.Ordinal)) is { } bad)
        {
            throw new PackageException($"{path}: trustedBakers entry '{bad}' must be a key fingerprint, SHA256:….");
        }
    }

    // Where a package ships: editor-only, compile-time-only and client-only exclude each other where they conflict.
    void ValidateShipping(string path)
    {
        if (EditorOnly && CompileTimeOnly)
        {
            throw new PackageException($"{path}: a package cannot be both editor-only and compile-time-only.");
        }

        if (ClientOnly && (EditorOnly || CompileTimeOnly))
        {
            throw new PackageException(
                $"{path}: a client-only package ships in games, so it cannot be editor- or compile-time-only.");
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
