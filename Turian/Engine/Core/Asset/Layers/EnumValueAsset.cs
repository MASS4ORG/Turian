namespace Turian.Engine.Core;

/// <summary>Authored identity and presentation for a value that can be interned at runtime.</summary>
[TypeId("35df23dd-ab7a-53ee-a580-741fa147caeb")]
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
[TypeId("beaa551f-3c02-5add-8879-a3aa9c39b72a")]
public sealed class LayerValueAsset : EnumValueAsset;

/// <summary>An independently identified tag that nodes can share.</summary>
[CreateAssetMenu(fileName: "Tag", path: "Layers/Tag")]
[TypeId("19ab83e0-67a6-51bb-9d57-b5e0b38a8765")]
public sealed class TagAsset : EnumValueAsset;
