using System.Text.Json.Nodes;

namespace Turian.Editor.Core;

/// <summary>How a node in a scene relates to prefabs, for the scene tree's icons.</summary>
public enum PrefabLink
{
    /// <summary>Not part of any prefab instance.</summary>
    None,

    /// <summary>A node the prefab of an enclosing instance provides.</summary>
    InstanceContent,

    /// <summary>The root of a prefab instance.</summary>
    Instance,

    /// <summary>The root of a prefab instance inside another instance.</summary>
    NestedInstance,

    /// <summary>The root of an instance of a prefab variant.</summary>
    VariantInstance,

    /// <summary>The root of an instance whose prefab is missing.</summary>
    Missing,
}

/// <summary>
/// Tells how scene nodes relate to prefabs. What it learns about each prefab — whether it exists, whether it is a
/// variant — is kept until <see cref="Reset"/>, since a scene holds many instances of few prefabs.
/// </summary>
/// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
public sealed class PrefabLinkClassifier(Func<Guid, string?> loadPrefab)
{
    // True for a variant, false for a plain prefab, null for a missing one.
    readonly Dictionary<Guid, bool?> variants = [];

    /// <summary>Forgets what was learned about prefabs, after one is saved, added or deleted.</summary>
    public void Reset() => variants.Clear();

    /// <summary>How <paramref name="node"/> relates to prefabs.</summary>
    /// <param name="node">A node in a loaded scene.</param>
    /// <returns>The node's prefab link.</returns>
    public PrefabLink Classify(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var inInstance = false;
        for (var ancestor = node.Parent; ancestor is not null && !inInstance; ancestor = ancestor.Parent)
            inInstance = ancestor.PrefabInstance is not null;

        if (node.PrefabInstance is not { } link) return inInstance ? PrefabLink.InstanceContent : PrefabLink.None;

        return IsVariant(link.Source.AssetId) switch
        {
            null => PrefabLink.Missing,
            true => PrefabLink.VariantInstance,
            false when inInstance => PrefabLink.NestedInstance,
            false => PrefabLink.Instance,
        };
    }

    bool? IsVariant(Guid prefabId)
    {
        if (variants.TryGetValue(prefabId, out var known)) return known;

        bool? variant;
        try
        {
            variant = loadPrefab(prefabId) is { } json
                ? JsonNode.Parse(json) is JsonObject root && root.ContainsKey(nameof(Node.PrefabInstance))
                : null;
        }
        catch (JsonException)
        {
            variant = null;
        }

        variants[prefabId] = variant;
        return variant;
    }
}
