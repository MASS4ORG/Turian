namespace Turian.Editor.Core;

/// <summary>
/// Represents a model asset that also stores import-time settings in its meta file.
/// </summary>
[TypeId("308dc8ac-3bc0-55f2-a12e-2c00bb440973")]
public class ModelImportAsset : ModelAsset
{
    /// <summary>
    /// Gets or sets the model import settings.
    /// </summary>
    public ModelImportSettings ImportSettings { get; set; } = new();
}
