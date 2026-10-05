namespace Turian.Engine.Core;

/// <summary>The project's ordered layer groups and tag identities used to build one runtime layout.</summary>
[CreateAssetMenu(fileName: "NodeLayerSettings", path: "Settings/Node Layers")]
[TypeId("90104bcf-6e4b-559e-98cf-4c428ca1221d")]
public sealed class NodeLayerSettings : ProjectSettingsAsset
{
    /// <summary>The ordered group references used to assign runtime slots.</summary>
    public List<LayerGroupAsset> Groups { get; set; } = [];

    /// <summary>The available tag references; at most 65,536 tags can be active.</summary>
    public List<TagAsset> Tags { get; set; } = [];

    /// <summary>The physics consumer's group reference, or null when the project does not use physics layers.</summary>
    public LayerGroupAsset? PhysicsGroup { get; set; }

    /// <summary>The rendering consumer's group reference, or null when the project does not use rendering layers.</summary>
    public LayerGroupAsset? RenderingGroup { get; set; }

    /// <summary>Reports unresolved references, duplicate identities, empty names, and capacity violations.</summary>
    public IReadOnlyList<string> Validate() => LayerAssetValidation.Validate(this);
}
