namespace Turian.Engine.Core;

/// <summary>
/// Provides access to asset content stored as a loose file on disk.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RealFileSystemProvider"/> class.
/// </remarks>
/// <param name="filePath">The absolute or resolved file path.</param>
public sealed class RealFileSystemProvider(string filePath) : IAssetFileProvider
{
    /// <summary>
    /// Gets or sets the file path of the asset.
    /// </summary>
    public string FilePath { get; set; } = filePath;

    /// <inheritdoc/>
    public string ContentKey => FilePath;

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
        if (!Exists)
        {
            throw new FileNotFoundException("Asset file was not found.", FilePath);
        }

        return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
