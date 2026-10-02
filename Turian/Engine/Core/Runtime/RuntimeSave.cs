namespace Turian.Engine.Core;

/// <summary>Runtime-only state and content identifiers captured at a simulation tick.</summary>
public sealed class RuntimeSaveSnapshot
{
    /// <summary>The simulation tick to resume from.</summary>
    public long Tick { get; set; }

    /// <summary>The explicit state of the game's deterministic random generator.</summary>
    public ulong RngState { get; set; }

    /// <summary>Game-owned runtime data, independent of authored object serialization.</summary>
    public JsonObject State { get; set; } = new();

    /// <summary>Authored DataAssets needed by this snapshot, resolved by the receiving session.</summary>
    public List<Guid> ContentIds { get; set; } = [];

    /// <summary>Namespaced extension data; unknown optional entries may be ignored.</summary>
    public Dictionary<string, RuntimeSaveExtension> Extensions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Namespaced runtime data whose required flag controls unknown-extension handling.</summary>
public sealed class RuntimeSaveExtension
{
    /// <summary>Whether a reader must understand this extension to resume.</summary>
    public bool Required { get; set; }

    /// <summary>Extension-owned JSON data.</summary>
    public JsonNode? Data { get; set; }
}

/// <summary>Content compatibility information supplied by the current runtime session.</summary>
public sealed class RuntimeSaveContent
{
    /// <summary>Fingerprint of the base game's content.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Fingerprints of active mods, keyed by stable mod identifier.</summary>
    public Dictionary<string, string> Mods { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A loaded snapshot with DataAssets owned by the receiving session's loader.</summary>
public sealed class LoadedRuntimeSave
{
    internal LoadedRuntimeSave(RuntimeSaveSnapshot snapshot, IReadOnlyDictionary<Guid, DataAsset> content,
        IReadOnlyList<string> unsupportedOptionalExtensions)
    {
        Snapshot = snapshot;
        Content = content;
        UnsupportedOptionalExtensions = unsupportedOptionalExtensions;
    }

    /// <summary>Runtime state to apply explicitly after the session has been initialized.</summary>
    public RuntimeSaveSnapshot Snapshot { get; }

    /// <summary>Resolved DataAssets, keyed by their authored identifiers.</summary>
    public IReadOnlyDictionary<Guid, DataAsset> Content { get; }

    /// <summary>Extensions retained in the snapshot but not understood by this session.</summary>
    public IReadOnlyList<string> UnsupportedOptionalExtensions { get; }
}

/// <summary>Reads and writes the versioned runtime save envelope without serializing authored assets.</summary>
public sealed class RuntimeSave
{
    /// <summary>The schema version written by this implementation.</summary>
    public const int CurrentVersion = 1;

    readonly Dictionary<int, Func<JsonObject, JsonObject>> migrations = [];
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };

    /// <summary>Registers a migration from one version to the next; existing registrations cannot be replaced.</summary>
    public void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (fromVersion < 0 || fromVersion >= CurrentVersion || !migrations.TryAdd(fromVersion, migrate))
            throw new ArgumentOutOfRangeException(nameof(fromVersion), "Migration must start at an unregistered older version.");
    }

    /// <summary>Captures a JSON envelope containing only runtime state and content identifiers.</summary>
    public string Write(RuntimeSaveSnapshot snapshot, RuntimeSaveContent content)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(content);
        Validate(snapshot, content);
        return JsonSerializer.Serialize(new Envelope(CurrentVersion, content, snapshot), Options);
    }

    /// <summary>
    /// Validates a save and resolves its authored content through the supplied session loader.
    /// Unknown required extensions and incompatible content are rejected before resolution.
    /// </summary>
    public async Task<LoadedRuntimeSave> ReadAsync(
        string json, RuntimeSaveContent expectedContent, IAssetLoader loader,
        IReadOnlySet<string>? supportedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(expectedContent);
        ArgumentNullException.ThrowIfNull(loader);

        var envelope = DeserializeEnvelope(ParseAndMigrate(json));
        Validate(envelope.Snapshot, envelope.Content);
        Validate(expectedContent);
        ValidateContentMatch(envelope.Content, expectedContent);
        var unsupported = CheckExtensions(envelope.Snapshot.Extensions, supportedExtensions);
        var resolved = await ResolveContentAsync(envelope.Snapshot.ContentIds, loader);
        return new LoadedRuntimeSave(envelope.Snapshot, resolved, unsupported);
    }

    JsonObject ParseAndMigrate(string json)
    {
        var root = ParseRoot(json);
        if (root["Version"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            throw new InvalidDataException("Runtime save is missing a valid numeric version.");
        if (version < 0 || version > CurrentVersion)
            throw new InvalidDataException($"Unsupported runtime save version {version}.");
        return MigrateRoot(root, version);
    }

    static JsonObject ParseRoot(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("Runtime save must be a JSON object.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid runtime save JSON.", error);
        }
    }

    JsonObject MigrateRoot(JsonObject root, int version)
    {
        while (version < CurrentVersion)
        {
            if (!migrations.TryGetValue(version, out var migrate))
                throw new InvalidDataException($"No runtime save migration from version {version}.");
            root = Migrate(migrate, root, version);
            version++;
            root["Version"] = version;
        }
        return root;
    }

    // Migrations run on a parsed copy, so a failing step leaves the save text and the session untouched.
    static JsonObject Migrate(Func<JsonObject, JsonObject> migrate, JsonObject root, int version)
    {
        try
        {
            return migrate(root) ?? throw new InvalidDataException($"Migration from version {version} returned null.");
        }
        catch (Exception error) when (error is not InvalidDataException)
        {
            throw new InvalidDataException($"Runtime save migration from version {version} failed.", error);
        }
    }

    static Envelope DeserializeEnvelope(JsonObject root)
    {
        ValidateEnvelopeShape(root);
        try
        {
            var envelope = root.Deserialize<Envelope>(Options)
                ?? throw new InvalidDataException("Runtime save envelope is empty.");
            if (envelope.Version != CurrentVersion || envelope.Content is null || envelope.Snapshot is null)
                throw new InvalidDataException("Runtime save envelope is missing required data.");
            return envelope;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid runtime save envelope.", error);
        }
    }

    static void ValidateEnvelopeShape(JsonObject root)
    {
        if (root["Content"] is not JsonObject content ||
            content["Fingerprint"] is null || content["Mods"] is not JsonObject ||
            root["Snapshot"] is not JsonObject snapshot)
            throw new InvalidDataException("Runtime save envelope is missing required data.");
        ValidateSnapshotShape(snapshot);
    }

    static void ValidateSnapshotShape(JsonObject snapshot)
    {
        if (snapshot["Tick"] is null || snapshot["RngState"] is null ||
            snapshot["State"] is not JsonObject || snapshot["ContentIds"] is not JsonArray ||
            snapshot["Extensions"] is not JsonObject extensions ||
            extensions.Any(extension => extension.Value is not JsonObject entry || entry["Required"] is null))
            throw new InvalidDataException("Runtime save envelope is missing required data.");
    }

    static void ValidateContentMatch(RuntimeSaveContent saved, RuntimeSaveContent expected)
    {
        if (saved.Fingerprint != expected.Fingerprint || saved.Mods.Count != expected.Mods.Count ||
            saved.Mods.Any(mod => !expected.Mods.TryGetValue(mod.Key, out var hash) || hash != mod.Value))
            throw new InvalidDataException("Runtime save content or mod fingerprints do not match this session.");
    }

    static List<string> CheckExtensions(Dictionary<string, RuntimeSaveExtension> extensions,
        IReadOnlySet<string>? supported)
    {
        var unsupported = new List<string>();
        foreach (var extension in extensions)
        {
            if (supported?.Contains(extension.Key) == true) continue;
            if (extension.Value.Required)
                throw new InvalidDataException($"Unknown required runtime save extension '{extension.Key}'.");
            unsupported.Add(extension.Key);
        }
        return unsupported;
    }

    static async Task<IReadOnlyDictionary<Guid, DataAsset>> ResolveContentAsync(
        IEnumerable<Guid> ids, IAssetLoader loader)
    {
        var uniqueIds = ids.Distinct().ToArray();
        var assets = await Task.WhenAll(uniqueIds.Select(loader.LoadContentAsync<DataAsset>));
        var resolved = new Dictionary<Guid, DataAsset>();
        for (var index = 0; index < uniqueIds.Length; index++)
        {
            if (assets[index] is not { } asset)
                throw new InvalidDataException(
                    $"Runtime save content '{uniqueIds[index]}' could not be resolved in this session.");
            resolved[uniqueIds[index]] = asset;
        }
        return resolved;
    }

    static void Validate(RuntimeSaveSnapshot snapshot, RuntimeSaveContent content)
    {
        Validate(content);
        if (snapshot.Tick < 0 || snapshot.State is null || snapshot.ContentIds is null || snapshot.Extensions is null)
            throw new InvalidDataException("Runtime save snapshot has invalid or missing data.");
        if (snapshot.ContentIds.Contains(Guid.Empty))
            throw new InvalidDataException("Runtime save snapshot has an empty content id.");
        ValidateExtensions(snapshot.Extensions);
    }

    static void ValidateExtensions(Dictionary<string, RuntimeSaveExtension> extensions)
    {
        if (extensions.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null))
            throw new InvalidDataException("Runtime save snapshot has invalid or missing extensions.");
    }

    static void Validate(RuntimeSaveContent content)
    {
        if (string.IsNullOrWhiteSpace(content.Fingerprint) || content.Mods is null ||
            content.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.Key) || string.IsNullOrWhiteSpace(mod.Value)))
            throw new InvalidDataException("Runtime save requires content and mod fingerprints.");
    }

    sealed record Envelope(int Version, RuntimeSaveContent Content, RuntimeSaveSnapshot Snapshot);
}
