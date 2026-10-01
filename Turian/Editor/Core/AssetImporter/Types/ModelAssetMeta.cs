namespace Turian.Editor.Core;

/// <summary>
/// Base metadata contract for model assets.
/// </summary>
[TypeId("fb70814a-9e82-546d-911f-7a0ec6056780")]
[PublicAPI]
public class ModelAssetMeta : ModelAsset
{
    /// <summary>
    /// Gets or sets the model format.
    /// </summary>
    public ModelAssetFormat ModelFormat { get; set; } = ModelAssetFormat.Unknown;

    /// <summary>
    /// Gets or sets a value indicating whether materials should be imported.
    /// </summary>
    public bool ImportMaterials { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether tangents should be generated.
    /// </summary>
    public bool GenerateTangents { get; set; } = true;
}
