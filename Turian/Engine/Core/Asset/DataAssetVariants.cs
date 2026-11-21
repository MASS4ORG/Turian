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

    static readonly string[] LinkProperties = [baseProperty, overridesProperty];
    static readonly string[] ReservedOverrideProperties = ["Id", ObjectJsonSerializer<DataAsset>.TypeIdProperty, Property];

    /// <summary>Whether the text of a data asset file is a variant.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>True for a variant.</returns>
    public static bool IsVariant(string json) =>
        json.Contains($"\"{Property}\"", StringComparison.Ordinal)
        && JsonNode.Parse(json) is JsonObject root && root.ContainsKey(Property);

    /// <summary>The text of a complete payload: the file itself, or a variant resolved against its bases.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="readBase">Gives the text of another data asset's file by asset id, or null when it is unknown.</param>
    /// <param name="assetId">The expected asset id, when the payload comes from a catalog entry.</param>
    /// <returns>JSON a payload deserializes from.</returns>
    /// <exception cref="InvalidOperationException">A base is unknown, or the variants form a cycle.</exception>
    public static string Flatten(string json, Func<Guid, string?> readBase, Guid? assetId = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(readBase);

        return json.Contains($"\"{Property}\"", StringComparison.Ordinal)
            ? Resolve(JsonNode.Parse(json)!.AsObject(), readBase, [], assetId).ToJsonString()
            : json;
    }

    /// <summary>
    /// The text of another data asset's file, from the asset database: its imported copy, which exists in the editor
    /// and in a built game alike.
    /// </summary>
    /// <param name="database">The asset database, or null when no project is loaded.</param>
    /// <param name="id">The asset id.</param>
    /// <returns>The text, or null when the database does not know the asset.</returns>
    public static string? ReadFromDatabase(AssetDatabase? database, Guid id)
    {
        if (database is null || !database.TryGetAssetProvider(id, out var provider) || provider is null)
        {
            return null;
        }

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
        if (baseId == Guid.Empty || id == Guid.Empty || id == baseId)
        {
            throw new ArgumentException("A variant and its base must have distinct, nonempty asset ids.");
        }

        var baseRoot = JsonNode.Parse(baseJson)!.AsObject();
        var root = new JsonObject();
        if (baseRoot[ObjectJsonSerializer<DataAsset>.TypeIdProperty] is { } typeId)
        {
            root[ObjectJsonSerializer<DataAsset>.TypeIdProperty] = typeId.DeepClone();
        }

        root["Id"] = id.ToString();
        root[Property] = new JsonObject { [baseProperty] = baseId.ToString(), [overridesProperty] = new JsonObject() };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Applies edits from two serialized payloads to an existing variant's overrides.</summary>
    public static string UpdateOverrides(string variantJson, string originalPayload, string editedPayload)
    {
        var variant = JsonNode.Parse(variantJson)!.AsObject();
        var link = variant[Property] as JsonObject
                   ?? throw new InvalidOperationException("The DataAsset is not a variant.");
        CheckLink(link);
        var before = JsonNode.Parse(originalPayload)!.AsObject();
        var after = JsonNode.Parse(editedPayload)!.AsObject();
        if (!JsonNode.DeepEquals(variant["Id"], before["Id"]))
        {
            throw new InvalidOperationException("The DataAsset variant changed on disk.");
        }
        foreach (var reserved in ReservedOverrideProperties)
        {
            if (!JsonNode.DeepEquals(before[reserved], after[reserved]))
            {
                throw new InvalidOperationException($"A data asset variant cannot override '{reserved}'.");
            }
            before.Remove(reserved);
            after.Remove(reserved);
        }

        var overrides = link[overridesProperty] as JsonObject;
        var updated = overrides ?? new JsonObject();
        RecordEdits(updated, before, after);
        if (overrides is null)
        {
            link[overridesProperty] = updated;
        }
        return variant.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static void RecordEdits(JsonObject patch, JsonObject before, JsonObject after)
    {
        foreach (var (name, previous) in before)
        {
            if (!after.ContainsKey(name) && previous is not null)
            {
                patch[name] = null;
            }
        }
        foreach (var (name, value) in after)
        {
            before.TryGetPropertyValue(name, out var previous);
            if (JsonNode.DeepEquals(previous, value))
            {
                continue;
            }

            if (previous is JsonObject oldObject && value is JsonObject newObject)
            {
                var nested = patch[name] as JsonObject;
                var updated = nested ?? new JsonObject();
                RecordEdits(updated, oldObject, newObject);
                if (nested is null)
                {
                    patch[name] = updated;
                }
            }
            else
            {
                patch[name] = value?.DeepClone();
            }
        }
    }

    static JsonObject Resolve(JsonObject variant, Func<Guid, string?> readBase, List<Guid> chain, Guid? assetId)
    {
        if (variant[Property] is not JsonObject link)
        {
            return variant;
        }

        if (chain.Count >= maxDepth)
        {
            throw new InvalidOperationException("Data asset variants are nested too deeply.");
        }

        CheckLink(link);
        if (link[baseProperty] is not JsonValue baseValue || !baseValue.TryGetValue<string>(out var text)
            || !Guid.TryParse(text, out var baseId))
        {
            throw new InvalidOperationException($"A data asset variant must name its base with {Property}.{baseProperty}.");
        }

        var id = GetVariantId(variant, baseId, assetId);
        if (chain.Contains(baseId))
        {
            throw new InvalidOperationException($"Data asset variants form a cycle through {baseId}.");
        }

        var baseJson = readBase(baseId)
                       ?? throw new InvalidOperationException($"The base {baseId} of a data asset variant is not in the project.");
        chain.Add(baseId);
        var resolved = Resolve(JsonNode.Parse(baseJson)!.AsObject(), readBase, chain, baseId);

        ApplyOverrides(resolved, variant, link);
        resolved["Id"] = id.ToString();
        return resolved;
    }

    // A link member this version does not understand fails, so newer variant syntax is never silently ignored.
    static void CheckLink(JsonObject link)
    {
        if (link.Select(static member => member.Key).FirstOrDefault(static name => !LinkProperties.Contains(name))
            is { } unknown)
        {
            throw new InvalidOperationException($"A data asset variant has an unknown {Property} member '{unknown}'.");
        }

        if (link[overridesProperty] is { } overrides && overrides is not JsonObject)
        {
            throw new InvalidOperationException($"{Property}.{overridesProperty} must be an object.");
        }
    }

    static Guid GetVariantId(JsonObject variant, Guid baseId, Guid? expected)
    {
        if (variant["Id"] is not JsonValue value || !value.TryGetValue<string>(out var text) ||
            !Guid.TryParse(text, out var id) || id == Guid.Empty || id == baseId ||
            expected is { } expectedId && id != expectedId)
        {
            throw new InvalidOperationException("A data asset variant must have its own asset id.");
        }

        return id;
    }

    static void ApplyOverrides(JsonObject resolved, JsonObject variant, JsonObject link)
    {
        if (link[overridesProperty] is JsonObject overrides)
        {
            foreach (var reserved in ReservedOverrideProperties)
            {
                if (overrides.ContainsKey(reserved))
                {
                    throw new InvalidOperationException($"A data asset variant cannot override '{reserved}'.");
                }
            }

            Merge(resolved, overrides);
        }

        CheckType(resolved, variant);
    }

    static void CheckType(JsonObject resolved, JsonObject variant)
    {
        var type = ObjectJsonSerializer<DataAsset>.TypeIdProperty;
        if (variant[type] is not null &&
            (!Guid.TryParse(variant[type]?.ToString(), out var declared) ||
             !Guid.TryParse(resolved[type]?.ToString(), out var inherited) ||
             TypeRegistry.CanonicalId(declared) != TypeRegistry.CanonicalId(inherited)))
        {
            throw new InvalidOperationException("A data asset variant must have the same type as its base.");
        }
    }

    // RFC 7386: objects merge, anything else replaces, null removes.
    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Merge(existing, child);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }
}
