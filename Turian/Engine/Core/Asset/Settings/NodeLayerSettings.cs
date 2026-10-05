namespace Turian.Engine.Core;

/// <summary>The project's ordered layer groups and tag identities used to build one runtime layout.</summary>
[CreateAssetMenu(fileName: "NodeLayerSettings", path: "Settings/Node Layers")]
[TypeId("c2e378eb-0b0c-49aa-916c-aa9c8a219f21")]
public sealed class NodeLayerSettings : ProjectSettingsAsset
{
    /// <summary>The ordered group references used to assign runtime slots.</summary>
    public List<LayerGroupAsset> Groups { get; set; } = [];

    /// <summary>The available tag references; at most 65,536 tags can be active.</summary>
    public List<TagAsset> Tags { get; set; } = [];

    /// <summary>Reports unresolved references, duplicate identities, empty names, and capacity violations.</summary>
    public IReadOnlyList<string> Validate() => LayerAssetValidation.Validate(this);
}
