namespace Turian.Editor.Core;

/// <summary>
/// Represents a model asset that also stores import-time settings in its meta file.
/// </summary>
[TypeId("a3000002-0000-4000-8000-000000000005")]
public class ModelImportAsset : ModelAsset
{
    /// <summary>
    /// Gets or sets the model import settings.
    /// </summary>
    public ModelImportSettings ImportSettings { get; set; } = new();
}
