using System.Formats.Tar;
using System.IO.Compression;

namespace Turian.Editor.Core;

/// <summary>One asset of a <c>.unitypackage</c>.</summary>
/// <param name="Guid">The asset's Unity guid, which Turian keeps as the asset id.</param>
/// <param name="Pathname">Where the asset was in the Unity project, such as <c>Assets/Art/Crate.png</c>.</param>
/// <param name="AssetFile">The extracted content, or null for a folder.</param>
/// <param name="MetaText">The text of Unity's <c>.meta</c> file, or null.</param>
public sealed record UnityPackageAsset(Guid Guid, string Pathname, string? AssetFile, string? MetaText);

/// <summary>
/// Reads a <c>.unitypackage</c>: a gzipped tar holding, for every asset, a folder named by its guid with the content
/// (<c>asset</c>), its Unity meta (<c>asset.meta</c>) and its project path (<c>pathname</c>).
/// </summary>
public static class UnityPackageReader
{
    /// <summary>Unpacks a package into <paramref name="folder"/> and lists its assets.</summary>
    /// <param name="packagePath">The <c>.unitypackage</c> file.</param>
    /// <param name="folder">An empty scratch folder the content is unpacked into.</param>
    /// <returns>The assets, by Unity path.</returns>
    /// <exception cref="InvalidDataException">The file is not a Unity package.</exception>
    public static IReadOnlyList<UnityPackageAsset> Read(string packagePath, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
        {
            throw new InvalidDataException($"{packagePath} is not a readable Unity package: {ex.Message}", ex);
        }

        var assets = new List<UnityPackageAsset>();
        foreach (var directory in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            var pathFile = Path.Combine(directory, "pathname");
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var guid) || !File.Exists(pathFile)) continue;

            var pathname = File.ReadLines(pathFile).FirstOrDefault()?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(pathname)) continue;

            var content = Path.Combine(directory, "asset");
            var meta = Path.Combine(directory, "asset.meta");
            assets.Add(new UnityPackageAsset(guid, pathname, File.Exists(content) ? content : null,
                File.Exists(meta) ? File.ReadAllText(meta) : null));
        }

        return [.. assets.OrderBy(static a => a.Pathname, StringComparer.Ordinal)];
    }
}
