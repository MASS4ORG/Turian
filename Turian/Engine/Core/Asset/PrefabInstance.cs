using System.Text.Json.Nodes;

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
