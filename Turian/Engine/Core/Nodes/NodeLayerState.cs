namespace Turian.Engine.Core;

/// <summary>A durable explicit membership; absence of a reference means unassigned.</summary>
public readonly record struct LayerReference(Guid GroupId, Guid ValueId);

/// <summary>GUID-based memberships and tags suitable for scenes, prefabs, and gameplay saves.</summary>
public sealed class NodeLayerState
{
    /// <summary>Explicit memberships, including explicit assignments to a group's current default.</summary>
    public List<LayerReference> Layers { get; set; } = [];

    /// <summary>Stable tag identities; missing tags are retained for later resolution.</summary>
    public List<Guid> Tags { get; set; } = [];

    /// <summary>Assigns a value by identity, retaining the reference even when it is the current default.</summary>
    public void SetLayer(Guid groupId, Guid valueId)
    {
        if (groupId == Guid.Empty || valueId == Guid.Empty)
            throw new ArgumentException("Explicit layer assignments require nonempty group and value identities.");
        var reference = new LayerReference(groupId, valueId);
        var index = Layers.FindIndex(layer => layer.GroupId == groupId);
        if (index < 0) Layers.Add(reference);
        else Layers[index] = reference;
    }

    /// <summary>Clears an assignment so the group follows its default at the next layout binding.</summary>
    public void ClearLayer(Guid groupId) => Layers.RemoveAll(layer => layer.GroupId == groupId);

    /// <summary>Resolves explicit identities into session-scoped bytes without changing durable references.</summary>
    public NodeLayers ResolveLayers(LayerInterningService layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (Layers is null) throw new InvalidDataException("Layer references cannot be null.");
        var memberships = new NodeLayers();
        var groups = new HashSet<Guid>();
        foreach (var reference in Layers)
        {
            if (reference.GroupId == Guid.Empty || reference.ValueId == Guid.Empty || !groups.Add(reference.GroupId))
                throw new InvalidDataException("Layer state requires nonempty identities and one assignment per group.");
            if (layout.TryGetGroupSlot(reference.GroupId, out var slot))
                memberships[slot] = layout.ResolveLayer(reference);
            else layout.WarnMissing(reference);
        }
        return memberships;
    }

    /// <summary>Resolves tag identities, omitting missing tags from runtime membership and retaining their GUIDs.</summary>
    public ushort[] ResolveTags(LayerInterningService layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (Tags is null) throw new InvalidDataException("Tag references cannot be null.");
        var ids = new SortedSet<ushort>();
        foreach (var tag in Tags)
        {
            if (tag == Guid.Empty) throw new InvalidDataException("Tag state requires nonempty identities.");
            if (layout.TryGetTagId(tag, out var id)) ids.Add(id);
            else layout.WarnMissingTag(tag);
        }
        return [.. ids];
    }
}
