namespace Turian.Engine.Core;

/// <summary>A named layer whose identity and serialized index survive settings list reordering.</summary>
public sealed class LayerSlot
{
    /// <summary>The stable identity of this slot in its layer space.</summary>
    [ReadOnly]
    public Guid Id { get; init; }

    /// <summary>The fixed index scenes and prefabs store.</summary>
    [ReadOnly]
    public int Index { get; init; }

    /// <summary>The editable display name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Defines the project's tags and independent physics and rendering layer spaces.</summary>
[CreateAssetMenu(fileName: "TagsAndLayersSettings", path: "Settings/Tags and Layers")]
[TypeId("f39695dd-31f0-49da-a601-4b7b742baf12")]
public sealed class TagsAndLayersSettings : ProjectSettingsAsset
{
    static readonly Guid PhysicsId = new("aa2a272a-4b01-40a2-870b-9b4284cbecad");
    static readonly Guid RenderId = new("e106d8c6-a313-43c1-a05a-fc4b8ad0b018");

    /// <summary>The tag names available to nodes; entries remain strings for future hierarchical tags.</summary>
    public List<string> Tags { get; set; } = ["Untagged"];

    /// <summary>The physics slots, identified by Index rather than their list position.</summary>
    public List<LayerSlot> PhysicsLayers { get; set; } = Defaults(PhysicsId);

    /// <summary>The rendering slots, identified by Index rather than their list position.</summary>
    public List<LayerSlot> RenderLayers { get; set; } = Defaults(RenderId);

    /// <summary>Finds a named physics slot by its stored index.</summary>
    public LayerSlot? FindPhysicsLayer(int index) => Find(PhysicsLayers, index);

    /// <summary>Finds a named rendering slot by its stored index.</summary>
    public LayerSlot? FindRenderLayer(int index) => Find(RenderLayers, index);

    /// <summary>Reports invalid names, identities and indices without changing saved settings.</summary>
    public IReadOnlyList<string> Validate() =>
    [
        .. ValidateNames(Tags, "Tag"),
        .. ValidateLayers(PhysicsLayers, "Physics"),
        .. ValidateLayers(RenderLayers, "Render"),
    ];

    internal static LayerSlot DefaultLayer(bool physics) => new()
    {
        Id = AssetIdFactory.Derive(physics ? PhysicsId : RenderId, "layer:0"),
        Index = 0,
        Name = "Default",
    };

    static List<LayerSlot> Defaults(Guid space) =>
    [
        .. Enumerable.Range(0, 32).Select(index => new LayerSlot
        {
            Id = AssetIdFactory.Derive(space, $"layer:{index}"),
            Index = index,
            Name = index == 0 ? "Default" : $"Layer {index}",
        }),
    ];

    static LayerSlot? Find(List<LayerSlot> layers, int index) => (uint)index < 32
        ? layers.FirstOrDefault(layer => layer.Index == index && !string.IsNullOrWhiteSpace(layer.Name)
                                        && layer.Id != Guid.Empty)
        : null;

    static IEnumerable<string> ValidateNames(IEnumerable<string> names, string space)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) yield return $"{space} names cannot be empty.";
            else if (!seen.Add(name)) yield return $"{space} name '{name}' is duplicated.";
        }
    }

    static IEnumerable<string> ValidateLayers(List<LayerSlot> layers, string space)
    {
        foreach (var warning in ValidateNames(layers.Select(layer => layer.Name), space)) yield return warning;
        var indices = new HashSet<int>();
        var ids = new HashSet<Guid>();
        foreach (var layer in layers)
        {
            if ((uint)layer.Index >= 32) yield return $"{space} layer index {layer.Index} must be between 0 and 31.";
            if (!indices.Add(layer.Index)) yield return $"{space} layer index {layer.Index} is duplicated.";
            if (layer.Id == Guid.Empty || !ids.Add(layer.Id)) yield return $"{space} layer identity is empty or duplicated.";
        }

        if (Find(layers, 0) is not { Name: "Default" }) yield return $"{space} layer 0 must be named Default.";
    }
}
