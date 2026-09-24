namespace Turian.Engine.Core;

/// <summary>
/// Provides access to asset content stored as a loose file on disk.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LooseFileAssetProvider"/> class.
/// </remarks>
/// <param name="filePath">The absolute or project-resolved path of the file.</param>
/// <param name="contentKey">The logical content key associated with this file.</param>
public sealed class LooseFileAssetProvider(string filePath, string? contentKey = null) : IAssetFileProvider
{
    /// <summary>
    /// Gets or sets the file path of the asset.
    /// </summary>
    public string FilePath { get; set; } = filePath;

    /// <inheritdoc/>
    public string ContentKey { get; } = string.IsNullOrWhiteSpace(contentKey) ? filePath : contentKey;

    /// <inheritdoc/>
    public AssetStorageKind StorageKind => AssetStorageKind.LooseFile;

    /// <inheritdoc/>
    public bool Exists => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    /// <inheritdoc/>
    public long? Length
    {
        get
        {
            if (!Exists)
            {
                return null;
            }

            try
            {
                return new FileInfo(FilePath).Length;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc/>
    public Stream GetAssetStream()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException("Asset file path is not configured.");
        }

        if (!File.Exists(FilePath))
        {
            throw new FileNotFoundException("Asset file was not found.", FilePath);
        }

        return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
