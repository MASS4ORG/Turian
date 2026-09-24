namespace Turian.Editor.Core;

/// <summary>
/// Describes an asset importer capable of recognizing a source file,
/// creating its corresponding asset metadata object, and providing
/// default import settings for that asset type.
/// </summary>
public interface IAssetImporter
{
    /// <summary>
    /// Determines whether this importer can handle the specified asset file.
    /// </summary>
    /// <param name="filePath">Absolute or project-relative path of the source asset file.</param>
    /// <returns><c>true</c> when the importer supports the file; otherwise <c>false</c>.</returns>
    bool IsValid(string filePath);

    /// <summary>
    /// Creates the asset metadata object for the specified source file.
    /// </summary>
    /// <param name="filePath">Absolute or project-relative path of the source asset file.</param>
    /// <returns>The asset metadata instance to serialize into the asset meta file.</returns>
    Asset CreateAsset(string filePath);

    /// <summary>
    /// Gets a stable identifier describing the importer kind.
    /// This value can be stored in meta files to track which importer owns the asset.
    /// </summary>
    string ImporterId => GetType().Name;

    /// <summary>
    /// Version of the cache artifacts this importer produces. An asset whose import manifest
    /// records a different value is reimported even when its source and settings are unchanged.
    /// </summary>
    int Version => 1;

    /// <summary>
    /// Creates a serializable object that contains importer-specific settings
    /// for the specified source file.
    /// Examples include texture compression, model import options, or audio settings.
    /// </summary>
    /// <param name="filePath">Absolute or project-relative path of the source asset file.</param>
    /// <returns>
    /// A serializable settings object, or <c>null</c> when the importer has no extra settings.
    /// </returns>
    object? CreateImportSettings(string filePath) => null;

    /// <summary>
    /// The object whose members the inspector draws as this asset's import settings, taken from the
    /// asset's own metadata so an edit lands where the importer already reads it from. Returning
    /// <c>null</c> — the default — means the kind has nothing to configure, which is the honest
    /// answer for a file that is copied through unchanged.
    /// </summary>
    /// <param name="asset">The asset metadata loaded from the <c>.meta</c> file.</param>
    /// <returns>The settings object to edit, or <c>null</c> when there is none.</returns>
    object? ImportSettingsFor(Asset asset) => null;

    /// <summary>
    /// The object an authored file holds — a data asset's payload, a material — for a kind whose
    /// source is written by the studio rather than by another tool. The inspector edits it directly
    /// and saving serializes it back over the file. <c>null</c>, the default, for imported files.
    /// </summary>
    /// <param name="asset">The asset metadata loaded from the <c>.meta</c> file.</param>
    /// <param name="sourcePath">Absolute path of the source file.</param>
    /// <returns>The authored object, or <c>null</c> when the file is imported rather than authored.</returns>
    IdClass? LoadAuthoredContent(Asset asset, string sourcePath) => null;

    /// <summary>
    /// Indicates whether an existing meta file should be regenerated when the source asset changes.
    /// </summary>
    /// <param name="filePath">Absolute or project-relative path of the source asset file.</param>
    /// <returns><c>true</c> to refresh metadata on changes; otherwise <c>false</c>.</returns>
    bool ShouldReimport(string filePath) => true;

    /// <summary>
    /// Returns child assets that should be registered alongside the primary asset.
    /// For glTF this yields one <see cref="MaterialAsset"/> per material and one
    /// <see cref="TextureAsset"/> per image referenced by the file. Each child
    /// asset's id must be deterministic (derived from the parent id + slot via
    /// <see cref="AssetIdFactory.Derive"/>) so re-importing produces stable references.
    /// </summary>
    /// <param name="parentAssetId">Id of the primary asset returned by <see cref="CreateAsset"/>.</param>
    /// <param name="filePath">Absolute path of the source asset file.</param>
    /// <param name="context">
    /// The pipeline, used to bind files the source references — its textures — to their own asset ids.
    /// </param>
    /// <returns>Zero or more child assets. Default implementation yields nothing.</returns>
    IEnumerable<Asset> CreateChildAssets(Guid parentAssetId, string filePath, IAssetImportContext context) => [];

    /// <summary>
    /// Returns the payload stored for a child asset, or <c>null</c> to store the child's own
    /// serialized metadata. Used by importers whose children carry content of their own, such as
    /// the <see cref="Prefab"/> a model file produces.
    /// </summary>
    /// <param name="parentAssetId">Id of the primary asset the child belongs to.</param>
    /// <param name="child">The child asset returned by <see cref="CreateChildAssets"/>.</param>
    /// <param name="filePath">Absolute path of the source asset file.</param>
    string? CreateChildAssetContent(Guid parentAssetId, Asset child, string filePath) => null;

    /// <summary>
    /// Returns a binary payload for a child asset, such as an image embedded in a model file, or
    /// <c>null</c> to fall back to <see cref="CreateChildAssetContent"/>.
    /// </summary>
    /// <param name="parentAssetId">Id of the primary asset the child belongs to.</param>
    /// <param name="child">The child asset returned by <see cref="CreateChildAssets"/>.</param>
    /// <param name="filePath">Absolute path of the source asset file.</param>
    byte[]? CreateChildAssetBinaryContent(Guid parentAssetId, Asset child, string filePath) => null;

    /// <summary>
    /// Base name of the artifact the asset database resolves as an asset's primary content.
    /// </summary>
    const string PrimaryArtifactName = "primary";

    /// <summary>
    /// Build targets this importer bakes a separate artifact for, in the order
    /// <see cref="ImportToCache"/> returns them. Targets exist because some payloads are not
    /// portable across platforms — GPU texture compression above all.
    /// </summary>
    /// <remarks>
    /// The default is empty: one artifact serves every target, which is true of everything but
    /// textures. When it is non-empty the first entry must be the target the editor itself
    /// consumes, since that artifact becomes the asset's primary content.
    /// </remarks>
    IReadOnlyList<string> BuildTargets => [];

    /// <summary>
    /// Writes the asset's cache artifacts into <paramref name="importDirectory"/>, which the
    /// caller has already created and emptied. The default implementation copies the source file.
    /// </summary>
    /// <param name="asset">The asset metadata being imported.</param>
    /// <param name="sourcePath">Absolute path of the source asset file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>
    /// The artifact file names relative to <paramref name="importDirectory"/>. The first entry
    /// is the primary artifact.
    /// </returns>
    IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory) =>
        CopySourceToCache(sourcePath, importDirectory);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into <paramref name="importDirectory"/> under the
    /// primary artifact name, keeping the source extension.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the source asset file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>A single-entry list holding the copied file name.</returns>
    static IReadOnlyList<string> CopySourceToCache(string sourcePath, string importDirectory)
    {
        var fileName = $"{PrimaryArtifactName}{Path.GetExtension(sourcePath)}";
        File.Copy(sourcePath, Path.Combine(importDirectory, fileName), overwrite: true);
        return [fileName];
    }
}
