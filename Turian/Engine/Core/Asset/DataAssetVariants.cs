namespace Turian.Engine.Core;

/// <summary>
/// A data asset that is another data asset with some values replaced, the way a prefab variant is a prefab with
/// overrides. Its file holds <c>__Variant</c>: the base asset's id and the properties to replace; everything else
/// comes from the base, so a change to the base reaches every variant that did not override it.
/// </summary>
public static class DataAssetVariants
{
    /// <summary>The property that marks a variant file.</summary>
    public const string Property = "__Variant";

    const string baseProperty = "Base";
    const string overridesProperty = "Overrides";
    const int maxDepth = 16;

    // What identifies a payload is the variant's own, never the base's.
    static readonly string[] OwnProperties = ["Id"];

    /// <summary>Whether the text of a data asset file is a variant.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>True for a variant.</returns>
    public static bool IsVariant(string json) =>
        json.Contains($"\"{Property}\"", StringComparison.Ordinal)
        && JsonNode.Parse(json) is JsonObject root && root.ContainsKey(Property);

    /// <summary>The text of a complete payload: the file itself, or a variant resolved against its bases.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="readBase">Gives the text of another data asset's file by asset id, or null when it is unknown.</param>
    /// <returns>JSON a payload deserializes from.</returns>
    /// <exception cref="InvalidOperationException">A base is unknown, or the variants form a cycle.</exception>
    public static string Flatten(string json, Func<Guid, string?> readBase)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(readBase);

        return json.Contains($"\"{Property}\"", StringComparison.Ordinal)
            ? Resolve(JsonNode.Parse(json)!.AsObject(), readBase, []).ToJsonString()
            : json;
    }

    /// <summary>
    /// The text of another data asset's file, from the asset database: its imported copy, which exists in the editor
    /// and in a built game alike.
    /// </summary>
    /// <param name="id">The asset id.</param>
    /// <returns>The text, or null when the database does not know the asset.</returns>
    public static string? ReadFromDatabase(Guid id)
    {
        if (!AssetDatabase.TryGetInstance(out var database) || database is null
            || !database.TryGetAssetProvider(id, out var provider) || provider is null)
            return null;

        using var reader = new StreamReader(provider.GetAssetStream());
        return reader.ReadToEnd();
    }

    /// <summary>Text of a variant file that has a base and no overrides yet.</summary>
    /// <param name="baseId">The base asset's id.</param>
    /// <param name="baseJson">The base asset's file, which supplies the payload type.</param>
    /// <param name="id">The variant's own id.</param>
    /// <returns>The JSON to write.</returns>
    public static string Create(Guid baseId, string baseJson, Guid id)
    {
        var baseRoot = JsonNode.Parse(baseJson)!.AsObject();
        var root = new JsonObject();
        if (baseRoot[ObjectJsonSerializer<DataAsset>.TypeIdProperty] is { } typeId)
            root[ObjectJsonSerializer<DataAsset>.TypeIdProperty] = typeId.DeepClone();
        root["Id"] = id.ToString();
        root[Property] = new JsonObject { [baseProperty] = baseId.ToString(), [overridesProperty] = new JsonObject() };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static JsonObject Resolve(JsonObject variant, Func<Guid, string?> readBase, List<Guid> chain)
    {
        if (variant[Property] is not JsonObject link) return variant;
        if (chain.Count >= maxDepth) throw new InvalidOperationException("Data asset variants are nested too deeply.");

        if (link[baseProperty]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var baseId))
            throw new InvalidOperationException($"A data asset variant must name its base with {Property}.{baseProperty}.");
        if (chain.Contains(baseId))
            throw new InvalidOperationException($"Data asset variants form a cycle through {baseId}.");

        var baseJson = readBase(baseId)
                       ?? throw new InvalidOperationException($"The base {baseId} of a data asset variant is not in the project.");
        chain.Add(baseId);
        var resolved = Resolve(JsonNode.Parse(baseJson)!.AsObject(), readBase, chain);

        if (link[overridesProperty] is JsonObject overrides) Merge(resolved, overrides);
        foreach (var own in OwnProperties)
        {
            if (variant[own] is { } value) resolved[own] = value.DeepClone();
        }

        return resolved;
    }

    // RFC 7386: objects merge, anything else replaces, null removes.
    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is null) target.Remove(name);
            else if (value is JsonObject child && target[name] is JsonObject existing) Merge(existing, child);
            else target[name] = value.DeepClone();
        }
    }
}
