namespace Turian.Engine.Physics;

public sealed partial class PhysicsScene
{
    /// <summary>The project's physics layers, or the default ones outside a project.</summary>
    IReadOnlyList<LayerSlot> PhysicsLayers => (layers?.Settings ?? new TagsAndLayersSettings()).PhysicsLayers;

    /// <summary>The index of the physics layer with this name, or -1 when none has it, as in Unity.</summary>
    public int NameToLayer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return PhysicsLayers.FirstOrDefault(layer => layer.Name == name)?.Index ?? -1;
    }

    /// <summary>The name of a physics layer, or an empty string for an unnamed or invalid index.</summary>
    public string LayerToName(int layer) =>
        PhysicsLayers.FirstOrDefault(slot => slot.Index == layer && (uint)layer < 32)?.Name ?? string.Empty;

    /// <summary>A mask of the physics layers with these names; unknown names are skipped, as in Unity.</summary>
    public LayerMask GetMask(params ReadOnlySpan<string> names)
    {
        var mask = 0u;
        foreach (var name in names)
        {
            if (NameToLayer(name) is var layer and >= 0)
            {
                mask |= 1u << layer;
            }
        }

        return new LayerMask(mask);
    }
}
