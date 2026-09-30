namespace Turian.Editor.Core;

/// <summary>
/// The project's own import settings for assets that live in bricks, kept in
/// <c>ProjectSettings/PackageImportOverrides.json</c> as asset id → the meta properties to replace. A brick's files
/// are read-only, so a project that wants a texture at half size says so here instead of editing the brick, and the
/// choice survives every update of it.
/// </summary>
public sealed class PackageImportOverrides
{
    /// <summary>The file, relative to the project folder.</summary>
    public const string RelativePath = "ProjectSettings/PackageImportOverrides.json";

    // What identifies an asset must not be changed by an override.
    static readonly HashSet<string> Protected = new(StringComparer.Ordinal) { "Id", "__TypeId", "RelativePath" };

    readonly SortedDictionary<Guid, JsonObject> entries = [];

    /// <summary>The asset ids that have an override.</summary>
    public IEnumerable<Guid> Ids => entries.Keys;

    /// <summary>Reads a project's overrides; none when the file does not exist.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The overrides.</returns>
    /// <exception cref="InvalidDataException">The file is not a JSON object of asset id → object.</exception>
    public static PackageImportOverrides Load(string projectRoot)
    {
        var overrides = new PackageImportOverrides();
        var path = Path.Combine(projectRoot, RelativePath);
        if (!File.Exists(path)) return overrides;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                throw new InvalidDataException($"{path} must hold a JSON object.");

            foreach (var (key, value) in root)
            {
                if (!Guid.TryParse(key, out var id) || value is not JsonObject patch)
                    throw new InvalidDataException($"{path}: '{key}' must be an asset id with an object of settings.");
                overrides.entries[id] = patch.DeepClone().AsObject();
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
        }

        return overrides;
    }

    /// <summary>The override of one asset, or null.</summary>
    /// <param name="id">The asset id.</param>
    /// <returns>A copy of the properties to replace.</returns>
    public JsonObject? Get(Guid id) => entries.TryGetValue(id, out var patch) ? patch.DeepClone().AsObject() : null;

    /// <summary>Sets one property of an asset's override; a <c>null</c> value removes it, and an empty override goes away.</summary>
    /// <param name="id">The asset id.</param>
    /// <param name="property">The meta property name.</param>
    /// <param name="value">The new value, or null to fall back to the brick's setting.</param>
    /// <exception cref="ArgumentException">The property identifies the asset and cannot be overridden.</exception>
    public void Set(Guid id, string property, JsonNode? value)
    {
        if (Protected.Contains(property)) throw new ArgumentException($"{property} cannot be overridden.", nameof(property));

        if (value is null)
        {
            if (entries.TryGetValue(id, out var existing) && existing.Remove(property) && existing.Count == 0) entries.Remove(id);
            return;
        }

        if (!entries.TryGetValue(id, out var patch)) entries[id] = patch = [];
        patch[property] = value.DeepClone();
    }

    /// <summary>Writes the overrides, or deletes the file when none remain.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public void Save(string projectRoot)
    {
        var path = Path.Combine(projectRoot, RelativePath);
        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var root = new JsonObject();
        foreach (var (id, patch) in entries) root[id.ToString()] = patch.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The meta with the asset's override laid over it; the meta itself when it has none.</summary>
    /// <param name="id">The asset id.</param>
    /// <param name="metaJson">The meta's JSON.</param>
    /// <returns>The JSON to load the meta from.</returns>
    public string Apply(Guid id, string metaJson)
    {
        if (!entries.TryGetValue(id, out var patch)) return metaJson;

        var meta = JsonNode.Parse(metaJson)!.AsObject();
        foreach (var (property, value) in patch)
        {
            if (!Protected.Contains(property)) meta[property] = value?.DeepClone();
        }

        return meta.ToJsonString();
    }
}
