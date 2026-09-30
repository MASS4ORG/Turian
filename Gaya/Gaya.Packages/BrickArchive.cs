using System.IO.Compression;

namespace Gaya.Packages;

/// <summary>The result of packing a brick.</summary>
/// <param name="Path">The <c>.brick</c> file.</param>
/// <param name="Integrity">The file's content hash, <c>sha256-</c> + base64.</param>
/// <param name="Manifest">The packed brick's <c>package.json</c>.</param>
public sealed record BrickPackResult(string Path, string Integrity, PackageManifest Manifest);

/// <summary>
/// The <c>.brick</c> transport file: a zip of a package folder with <c>package.json</c> at its root. Packing is
/// reproducible, so the same folder always yields the same bytes and hash.
/// </summary>
public static class BrickArchive
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".brick";

    static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules",
    };

    /// <summary>The file name a brick is packed under: <c>id-version.brick</c>.</summary>
    /// <param name="manifest">The brick's manifest.</param>
    /// <returns>The file name.</returns>
    public static string FileNameFor(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return $"{manifest.Name}-{manifest.Version}{Extension}";
    }

    /// <summary>Packs the package folder at <paramref name="packageRoot"/> into <paramref name="outputDirectory"/>.</summary>
    /// <param name="packageRoot">The package folder.</param>
    /// <param name="outputDirectory">Where the <c>.brick</c> and its hash file are written.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <param name="precast">
    /// What the folder's <c>Precast~</c> payload was built with; recorded in the file's <c>package.json</c>, never in
    /// the folder's.
    /// </param>
    /// <returns>The packed file, its hash and the manifest.</returns>
    /// <exception cref="PackageException">The folder is not a valid package.</exception>
    public static BrickPackResult Pack(string packageRoot, string outputDirectory,
        IReadOnlyCollection<string>? reservedCategoryPrefixes = null, PackagePrecast? precast = null)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        var manifest = PackageManifest.Load(packageRoot, reservedCategoryPrefixes);
        if (precast is not null) manifest.Precast = precast;
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(Path.GetFullPath(outputDirectory), FileNameFor(manifest));

        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (relative, file) in Files(packageRoot))
            {
                var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTime;
                entry.ExternalAttributes = 0b110_100_100 << 16;
                using var target = entry.Open();
                if (relative == PackageManifest.FileName && precast is not null)
                {
                    target.Write(Encoding.UTF8.GetBytes(manifest.ToJson()));
                    continue;
                }

                using var source = File.OpenRead(file);
                source.CopyTo(target);
            }
        }

        var integrity = ComputeIntegrity(path);
        File.WriteAllText($"{path}.sha256", $"{Convert.ToHexStringLower(Convert.FromBase64String(integrity["sha256-".Length..]))}  {Path.GetFileName(path)}\n");
        return new BrickPackResult(path, integrity, manifest);
    }

    /// <summary>The <c>package.json</c> inside a <c>.brick</c> file.</summary>
    /// <param name="archivePath">The <c>.brick</c> file.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="PackageException">The file is not a brick.</exception>
    public static PackageManifest ReadManifest(string archivePath, IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(PackageManifest.FileName)
                        ?? throw new PackageException($"{archivePath} has no {PackageManifest.FileName} at its root.");
            using var reader = new StreamReader(entry.Open());
            return PackageManifest.Parse(reader.ReadToEnd(), $"{archivePath}!{PackageManifest.FileName}", reservedCategoryPrefixes);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageException($"{archivePath} is not a valid {Extension} file: {ex.Message}", ex);
        }
    }

    /// <summary>Extracts a <c>.brick</c> file into <paramref name="destination"/>, which must not exist or be empty.</summary>
    /// <param name="archivePath">The <c>.brick</c> file.</param>
    /// <param name="destination">The folder to extract into.</param>
    /// <exception cref="PackageException">The file is not a brick.</exception>
    public static void Extract(string archivePath, string destination)
    {
        try
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageException($"{archivePath} is not a valid {Extension} file: {ex.Message}", ex);
        }
    }

    /// <summary>A file's content hash, <c>sha256-</c> + base64.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The integrity string.</returns>
    public static string ComputeIntegrity(string path)
    {
        using var stream = File.OpenRead(path);
        return $"sha256-{Convert.ToBase64String(SHA256.HashData(stream))}";
    }

    static IEnumerable<(string Relative, string File)> Files(string packageRoot) =>
        Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(file => (Relative: System.IO.Path.GetRelativePath(packageRoot, file).Replace('\\', '/'), File: file))
            .Where(static f => !IsSkipped(f.Relative))
            .OrderBy(static f => f.Relative, StringComparer.Ordinal);

    static bool IsSkipped(string relative)
    {
        var segments = relative.Split('/');
        var name = segments[^1];
        return segments[..^1].Any(SkippedDirectories.Contains)
               || name.EndsWith(Extension, StringComparison.Ordinal)
               || name.EndsWith($"{Extension}.sha256", StringComparison.Ordinal)
               || name.EndsWith(".user", StringComparison.Ordinal)
               || name is ".DS_Store" or "Thumbs.db";
    }
}
