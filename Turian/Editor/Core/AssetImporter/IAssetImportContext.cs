namespace Turian.Editor.Core;

/// <summary>
/// The import pipeline as seen by an importer that needs to reference other files.
/// A model file names its textures by path; binding those to the texture files' own asset
/// ids keeps one asset per file on disk, so a texture stays swappable and is imported once
/// however many materials use it.
/// </summary>
public interface IAssetImportContext
{
    /// <summary>
    /// Ensures the file at <paramref name="absolutePath"/> has a meta file, is imported into the
    /// cache and is registered in the asset database, and returns its asset id.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the file to register.</param>
    /// <returns>The file's asset id, or <see cref="Guid.Empty"/> when it does not exist.</returns>
    Guid EnsureAsset(string absolutePath);

    /// <summary>
    /// Applies the color-space settings a texture's role implies to the texture's own meta file.
    /// A folder scan has no material context and tags every texture sRGB; a model that samples one
    /// as a normal or metallic-roughness map knows better. Only differing values are written.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the texture file.</param>
    /// <param name="isSrgb">Whether the texture is sampled in sRGB space.</param>
    /// <param name="flipGreenChannel">Whether the green channel is inverted, as DirectX normal maps are.</param>
    void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel);

    /// <summary>
    /// A context that registers nothing and returns <see cref="Guid.Empty"/> for every path.
    /// Importers fall back to whatever they can express without external assets.
    /// </summary>
    static IAssetImportContext None { get; } = new NullAssetImportContext();

    private sealed class NullAssetImportContext : IAssetImportContext
    {
        public Guid EnsureAsset(string absolutePath) => Guid.Empty;

        public void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel)
        {
        }
    }
}
