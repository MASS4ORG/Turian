namespace Turian.Engine.Core;

/// <summary>
/// Links a node to the <see cref="Prefab"/> it was instantiated from. Saved scenes store the instance as this link
/// plus its differences from the prefab; loading rebuilds the hierarchy from the current prefab, so prefab edits reach
/// every instance.
/// </summary>
/// <remarks>
/// Targets are object ids as they appear in the prefab (after the prefab's own nested instances are expanded), so
/// they stay valid however many times the prefab is instantiated. See <see cref="PrefabInstances"/>.
/// </remarks>
public sealed class PrefabInstance
{
    /// <summary>The prefab this instance was created from.</summary>
    public AssetReference<Prefab> Source { get; set; } = new();

    /// <summary>Member values that differ from the prefab. Empty once the instance is loaded.</summary>
    public List<PrefabOverride> Overrides { get; set; } = [];

    /// <summary>Children and components the instance adds to prefab nodes. Empty once the instance is loaded.</summary>
    public List<PrefabAddition> Added { get; set; } = [];

    /// <summary>Prefab nodes and components the instance removes. Empty once the instance is loaded.</summary>
    public List<Guid> Removed { get; set; } = [];
}

/// <summary>A serialized member of a prefab node or component replaced by an instance.</summary>
/// <param name="Target">The id of the node or component in the prefab.</param>
/// <param name="Member">The serialized member name, such as <c>Transform</c> or <c>Intensity</c>.</param>
/// <param name="Value">The member's JSON value on the instance.</param>
public sealed record PrefabOverride(Guid Target, string Member, JsonNode? Value);

/// <summary>A child node or a component an instance adds under a prefab node.</summary>
/// <param name="Parent">The id of the prefab node receiving it.</param>
/// <param name="Child">The added child node's JSON, or null for a component.</param>
/// <param name="Component">The added component's JSON, or null for a child.</param>
public sealed record PrefabAddition(Guid Parent, JsonObject? Child = null, JsonObject? Component = null);

/// <summary>What the prefab instances in a hierarchy change about their prefabs, by object id in the hierarchy.</summary>
public sealed class PrefabInstanceDiff
{
    /// <summary>Members of prefab objects whose value differs from the prefab.</summary>
    public HashSet<(Guid Object, string Member)> Overrides { get; } = [];

    /// <summary>Nodes and components an instance has that its prefab does not.</summary>
    public HashSet<Guid> Added { get; } = [];

    /// <summary>Prefab objects an instance no longer has, by the id they would have in it.</summary>
    public HashSet<Guid> Removed { get; } = [];

    /// <summary>Instance roots whose prefab could not be loaded.</summary>
    public HashSet<Guid> MissingPrefabs { get; } = [];

    /// <summary>Whether any member of the object differs from the prefab.</summary>
    /// <param name="objectId">A node or component id.</param>
    /// <returns>True when at least one of its members is overridden.</returns>
    public bool HasOverrides(Guid objectId) => Overrides.Any(entry => entry.Object == objectId);

    /// <summary>Whether anything differs from the prefabs at all.</summary>
    public bool IsEmpty => Overrides.Count == 0 && Added.Count == 0 && Removed.Count == 0;
}

/// <summary>What a new instance of a prefab holds.</summary>
/// <param name="Content">The expanded instance, with the ids its objects get in the instance.</param>
/// <param name="SourceIds">Each instance object id mapped to the id of the prefab object it comes from.</param>
public sealed record PrefabExpectation(JsonObject Content, IReadOnlyDictionary<Guid, Guid> SourceIds);
