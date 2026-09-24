namespace Turian.Editor.Core;

/// <summary>
/// Importer for image files. Bakes one <c>.amtex</c> container per build target holding exactly
/// what the GPU samples: DDS block data passes through untouched with its mip chain, while source
/// formats the GPU cannot sample decode to RGBA8 and generate mips at load.
/// </summary>
public class TextureAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<string> BuildTargets => TextureBuildTarget.Implemented;

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && TextureAssetFormatExtensions.FromFilePath(filePath) != TextureAssetFormat.Unknown;

    /// <summary>
    /// Creates the texture's metadata, seeding color space and normal-map convention from the
    /// filename role. A folder scan reaches a texture before any material that samples it, so
    /// guessing here is what stops every normal and ORM map from being tagged sRGB and reimported
    /// a second time once a model corrects it through
    /// <see cref="IAssetImportContext.ConfigureTexture"/>.
    /// </summary>
    /// <param name="filePath">Absolute or project-relative path of the image file.</param>
    public Asset CreateAsset(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var isNormalMap = HasRoleSuffix(filePath, "_Normal");
        var isLinearData = isNormalMap || HasRoleSuffix(filePath, "_Specular") || HasRoleSuffix(filePath, "_ORM");

        return new TextureAsset
        {
            RelativePath = filePath,
            IsSrgb = !isLinearData,

            // The DirectX convention travels with the role, not the container: Bistro's BC5 normals
            // are green-inverted and cannot be rewritten without recompressing, so the shader flips.
            FlipGreenChannel = isNormalMap,
        };
    }

    /// <summary>
    /// The texture's own metadata: color space and the green-channel flip sit beside
    /// <see cref="TextureAsset.ImportSettings"/>, and all three drive the bake.
    /// </summary>
    /// <param name="asset">The texture metadata.</param>
    /// <returns>The metadata itself, or null when it is not a texture.</returns>
    public object? ImportSettingsFor(Asset asset) => asset as TextureAsset;

    /// <summary>
    /// Bakes one artifact per entry in <see cref="BuildTargets"/>, in that order.
    /// </summary>
    /// <param name="asset">The texture metadata being imported.</param>
    /// <param name="sourcePath">Absolute path of the source image file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>The artifact file names, one per build target.</returns>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importDirectory);

        var texture = asset as TextureAsset ?? new TextureAsset();
        var source = File.ReadAllBytes(sourcePath);

        var artifacts = new List<string>(BuildTargets.Count);
        foreach (var target in BuildTargets)
        {
            var content = Bake(source, texture, target, sourcePath);
            var fileName = ArtifactFileName(target);
            TextureBlobWriter.Save(Path.Combine(importDirectory, fileName), content);
            artifacts.Add(fileName);
        }

        return artifacts;
    }

    /// <summary>
    /// Name of the artifact baked for <paramref name="target"/>. The target is part of the name so
    /// one import directory can hold every platform's variant side by side.
    /// </summary>
    /// <param name="target">A key from <see cref="TextureBuildTarget"/>.</param>
    public static string ArtifactFileName(string target) =>
        $"{IAssetImporter.PrimaryArtifactName}.{target}{TextureBlob.FileExtension}";

    /// <summary>
    /// Produces the blob for one target. Only <see cref="TextureBuildTarget.Pc"/> has an encoder:
    /// it keeps BC blocks as they are. ETC2 and ASTC encoders hook in here, transcoding the decoded
    /// pixels before the container is written.
    /// </summary>
    static TextureBlobContent Bake(byte[] source, TextureAsset texture, string target, string sourcePath)
    {
        var maxResolution = texture.ImportSettings.ResolveMaxResolution(target);

        if (DdsReader.IsDds(source))
        {
            var dds = DdsReader.Read(source, texture.IsSrgb);
            var (width, height, levels) = ApplyMaxResolution(dds.Width, dds.Height, dds.Levels, maxResolution);
            return new TextureBlobContent(dds.Format, width, height, texture.IsSrgb, levels);
        }

        var image = ImageResult.FromMemory(source, ColorComponents.RedGreenBlueAlpha)
                    ?? throw new InvalidDataException($"Could not decode image '{sourcePath}'");

        // Uncompressed sources carry no mip chain, so there is nothing to skip; the GPU generates
        // the chain at load and MaxResolution is left to a resampling step this importer does not do.
        var format = texture.IsSrgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
        return new TextureBlobContent(
            format,
            (uint)image.Width,
            (uint)image.Height,
            texture.IsSrgb,
            [image.Data]);
    }

    /// <summary>
    /// Drops leading mip levels until the longest edge fits <paramref name="maxResolution"/>.
    /// Lossless and free for block-compressed data, whose chain is already baked — and it keeps the
    /// skipped levels out of the cache, not just out of VRAM. Never drops the last level.
    /// </summary>
    static (uint Width, uint Height, IReadOnlyList<ReadOnlyMemory<byte>> Levels) ApplyMaxResolution(
        uint width,
        uint height,
        IReadOnlyList<ReadOnlyMemory<byte>> levels,
        int maxResolution)
    {
        if (maxResolution <= 0)
        {
            return (width, height, levels);
        }

        var skip = 0;
        while (skip < levels.Count - 1)
        {
            var (levelWidth, levelHeight) = TextureFormats.LevelExtent(width, height, skip);
            if (Math.Max(levelWidth, levelHeight) <= maxResolution) break;
            skip++;
        }

        if (skip == 0)
        {
            return (width, height, levels);
        }

        var (baseWidth, baseHeight) = TextureFormats.LevelExtent(width, height, skip);
        return (baseWidth, baseHeight, [.. levels.Skip(skip)]);
    }

    static bool HasRoleSuffix(string filePath, string suffix) =>
        Path.GetFileNameWithoutExtension(filePath).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
