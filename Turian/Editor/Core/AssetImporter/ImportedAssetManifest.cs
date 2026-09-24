namespace Turian.Editor.Core;

/// <summary>
/// Serialized import result for a cached asset.
/// </summary>
[PublicAPI]
public sealed class ImportedAssetManifest
{
    /// <summary>
    /// Gets or sets the asset identifier.
    /// </summary>
    public Guid AssetId { get; set; }

    /// <summary>
    /// Gets or sets the project-relative source asset path.
    /// </summary>
    public string SourceRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project-relative meta file path.
    /// </summary>
    public string MetaRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset type name.
    /// </summary>
    public string AssetTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the importer identifier.
    /// </summary>
    public string ImporterId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version of the import pipeline that produced the artifacts.
    /// </summary>
    public int PipelineVersion { get; set; }

    /// <summary>
    /// Gets or sets the version the importer itself declares through
    /// <see cref="IAssetImporter.Version"/>.
    /// </summary>
    public int ImporterVersion { get; set; }

    /// <summary>
    /// Gets or sets the source file content hash.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the import settings hash.
    /// </summary>
    public string SettingsHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary artifact file name inside the import directory.
    /// </summary>
    public string PrimaryArtifactFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the artifact file names produced for the import.
    /// </summary>
    public List<string> Artifacts { get; set; } = [];

    /// <summary>
    /// Gets or sets the artifact each build target consumes, keyed by
    /// <see cref="TextureBuildTarget"/>. Empty for importers whose single artifact serves every
    /// target; populated from <see cref="IAssetImporter.BuildTargets"/> for those that do not,
    /// so an export knows which variant to ship without re-running the importer.
    /// </summary>
    public Dictionary<string, string> TargetArtifacts { get; set; } = [];

    /// <summary>
    /// Gets or sets the import timestamp.
    /// </summary>
    public DateTimeOffset ImportedAtUtc { get; set; }
}
