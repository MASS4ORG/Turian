namespace Turian.Engine.Core;

/// <summary>Authored identity and presentation for a value that can be interned at runtime.</summary>
[TypeId("aa0c7d85-70db-4db4-aeaa-ea91d17f7608")]
public abstract class EnumValueAsset : DataAsset
{
    /// <summary>The editable display name; references use the asset identity.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The color shown by authoring tools.</summary>
    public Color32 Color { get; set; } = new(255, 255, 255);

    /// <summary>Explains the value's purpose to designers.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>A single membership value in a layer group.</summary>
[CreateAssetMenu(fileName: "LayerValue", path: "Layers/Value")]
[TypeId("b9ee2d03-11c2-4826-aac3-615e146972b7")]
public sealed class LayerValueAsset : EnumValueAsset;

/// <summary>An independently identified tag that nodes can share.</summary>
[CreateAssetMenu(fileName: "Tag", path: "Layers/Tag")]
[TypeId("9c948080-6463-4bb7-83e7-58999c5f9d94")]
public sealed class TagAsset : EnumValueAsset;
