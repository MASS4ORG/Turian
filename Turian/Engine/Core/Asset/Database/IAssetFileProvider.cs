namespace Turian.Engine.Core;

/// <summary>
/// Provides read access to resolved asset content regardless of the backing storage.
/// </summary>
public interface IAssetFileProvider
{
    /// <summary>
    /// Gets the logical content key for the resolved asset payload.
    /// </summary>
    string ContentKey { get; }

    /// <summary>
    /// Gets the backing storage kind used by this provider.
    /// </summary>
    AssetStorageKind StorageKind { get; }

    /// <summary>
    /// Gets a value indicating whether the content currently exists.
    /// </summary>
    bool Exists { get; }

    /// <summary>
    /// Gets the content length in bytes when it can be determined; otherwise <c>null</c>.
    /// </summary>
    long? Length { get; }

    /// <summary>
    /// Opens the asset content for reading.
    /// </summary>
    /// <returns>A readable stream for the asset payload.</returns>
    Stream GetAssetStream();
}

/// <summary>
/// Describes where resolved asset content is stored.
/// </summary>
public enum AssetStorageKind
{
    /// <summary>
    /// Storage kind is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Content is stored as a loose file on disk.
    /// </summary>
    LooseFile = 1,

    /// <summary>
    /// Content is stored inside an Open Asset Package (<c>.oap</c>) container.
    /// </summary>
    Oap = 2
}

/// <summary>
/// Represents a persisted asset catalog.
/// </summary>
[PublicAPI]
public sealed class AssetCatalog
{
    /// <summary>
    /// The current asset catalog format version.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets or sets the catalog format version.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Gets or sets the UTC timestamp when the catalog was generated.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the asset records.
    /// </summary>
    public List<AssetRecord> Records { get; set; } = [];
}

/// <summary>
/// Represents a catalog record describing a source asset and its resolved content.
/// </summary>
[PublicAPI]
public sealed class AssetRecord
{
    /// <summary>
    /// Gets or sets the unique asset identifier.
    /// </summary>
    public Guid AssetId { get; set; }

    /// <summary>
    /// Gets or sets the absolute project root path used to resolve relative content.
    /// </summary>
    public string ProjectRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset metadata type name.
    /// </summary>
    public string AssetTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifier of the asset this one was imported from, or
    /// <see cref="Guid.Empty"/> when the asset has its own source file.
    /// Child assets — the materials and textures a model file declares — have no source file
    /// and no <c>.meta</c> of their own; they live inside the parent's import directory and are
    /// re-emitted whenever the parent is reimported. Records whose parent no longer exists are
    /// dropped when the database is rebuilt.
    /// </summary>
    public Guid ParentAssetId { get; set; }

    /// <summary>
    /// Gets or sets the asset's labels, copied from its metadata so they can be queried without loading it.
    /// </summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>
    /// Gets or sets the source asset path relative to the project root.
    /// Example: <c>Assets/Models/ship.glb</c>.
    /// </summary>
    public string SourceRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source metadata path relative to the project root.
    /// Example: <c>Assets/Models/ship.glb.meta</c>.
    /// </summary>
    public string MetaRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary logical content key used to resolve the asset payload.
    /// </summary>
    public string PrimaryContentKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the imported primary artifact path relative to the project root or runtime root.
    /// For assets packed into an Open Asset Package, this points to the <c>.oap</c> file.
    /// </summary>
    public string ImportedRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the storage kind backing the resolved payload.
    /// </summary>
    public AssetStorageKind StorageKind { get; set; } = AssetStorageKind.Unknown;

    /// <summary>
    /// Gets or sets the last known source content hash used to produce the imported artifact.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the import settings used to produce the imported artifact.
    /// </summary>
    public string SettingsHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the importer identifier that generated the imported artifact.
    /// </summary>
    public string ImporterId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the importer version used to generate the imported artifact.
    /// </summary>
    public int ImporterVersion { get; set; }

    /// <summary>
    /// Gets or sets the list of artifact keys produced for this asset.
    /// </summary>
    public List<string> Artifacts { get; set; } = [];

    /// <summary>
    /// Gets or sets the artifacts that exist per build target, keyed by
    /// <see cref="TextureBuildTarget"/>. Empty for assets whose single artifact serves every
    /// platform, which is everything but textures: GPU texture compression is not portable, so one
    /// texture asset owns a set of baked variants and the running platform picks one.
    /// <see cref="ImportedRelativePath"/> already names the variant for the running target; this
    /// map is what an export for a different target reads.
    /// </summary>
    public Dictionary<string, string> TargetArtifacts { get; set; } = [];

    /// <summary>
    /// Gets or sets the fully-qualified names of Component types present in the asset content.
    /// Only meaningful for prefab-like assets; empty for other asset kinds. Populated at import
    /// time so editor pickers and runtime lookups can filter prefabs by component shape without
    /// re-parsing their content.
    /// </summary>
    public List<string> ComponentTypes { get; set; } = [];

    /// <summary>
    /// Creates the default primary content key for the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <returns>A stable logical primary content key.</returns>
    public static string CreatePrimaryContentKey(Guid assetId)
    {
        return $"{assetId:N}:primary";
    }

    /// <summary>
    /// Resolves the absolute path to the asset payload using the stored project root.
    /// </summary>
    /// <returns>The absolute path when it can be resolved; otherwise an empty string.</returns>
    public string ResolveContentPath()
    {
        if (string.IsNullOrWhiteSpace(ImportedRelativePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(ImportedRelativePath))
        {
            return ImportedRelativePath;
        }

        if (string.IsNullOrWhiteSpace(ProjectRootPath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(Path.Combine(ProjectRootPath, ImportedRelativePath));
    }
}
