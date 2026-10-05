namespace Turian.Engine.Core;

/// <summary>An ordered manifest of up to 256 layer values with an explicit fallback identity.</summary>
[CreateAssetMenu(fileName: "LayerGroup", path: "Layers/Group")]
[TypeId("9b2c918e-3b11-4e3c-b33b-a1c2a0b20f8e")]
public sealed class LayerGroupAsset : DataAsset
{
    /// <summary>The unique group name within the project.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The ordered references used to derive compact runtime indices.</summary>
    public List<LayerValueAsset> Values { get; set; } = [];

    /// <summary>The value used by unassigned nodes and missing memberships; it must belong to Values.</summary>
    public LayerValueAsset? DefaultValue { get; set; }
}
