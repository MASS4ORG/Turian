using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>One asset copied out of a brick.</summary>
/// <param name="Source">The asset's path inside the brick, relative to the brick folder.</param>
/// <param name="Target">The copy's path, relative to the project folder.</param>
/// <param name="OldId">The brick asset's id.</param>
/// <param name="NewId">The copy's id.</param>
public sealed record CopiedAsset(string Source, string Target, Guid OldId, Guid NewId);

/// <summary>
/// Copies assets out of a brick into the project's own <c>Assets</c> folder: the escape hatch for a project that
/// wants to change one asset without forking the whole brick. The copies get new ids, so they are the project's own
/// and stay put when the brick updates.
/// </summary>
public static class BrickAssetCopy
{
    static readonly string[] RemappedExtensions = [".prefab", ".dataasset", ".asset", ".json", ".ui", ".uss", ".meta", ".mat"];

    /// <summary>The brick's importable assets: every file with a <c>.meta</c> beside it, outside <c>~</c> folders.</summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>Paths relative to the brick folder, using <c>/</c>.</returns>
    public static IReadOnlyList<string> Assets(ResolvedPackage brick) =>
        [.. Directory.EnumerateFiles(brick.RootPath, "*.meta", SearchOption.AllDirectories)
            .Select(meta => Path.GetRelativePath(brick.RootPath, meta[..^".meta".Length]).Replace('\\', '/'))
            .Where(static path => !path.Split('/').SkipLast(1).Any(static s => s.EndsWith('~')))
            .Order(StringComparer.Ordinal)];

    /// <summary>Copies assets out of a brick.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="brick">The installed brick.</param>
    /// <param name="assets">The assets to copy, relative to the brick folder.</param>
    /// <param name="destination">The folder, relative to <c>Assets</c>, the copies go into.</param>
    /// <param name="remapReferences">
    /// Whether the project's own files that point at a brick asset are rewritten to point at its copy. A project that
    /// only wants the copy, detached from the brick, leaves this off.
    /// </param>
    /// <returns>What was copied, with the ids that changed.</returns>
    /// <exception cref="PackageException">An asset is not in the brick.</exception>
    public static IReadOnlyList<CopiedAsset> Copy(string projectRoot, ResolvedPackage brick, IEnumerable<string> assets,
        string destination, bool remapReferences = false)
    {
        var folder = Path.Combine(projectRoot, "Assets", destination);
        Directory.CreateDirectory(folder);
        var copied = new List<CopiedAsset>();

        foreach (var asset in assets)
        {
            var source = Path.Combine(brick.RootPath, asset);
            var sourceMeta = $"{source}.meta";
            if (!File.Exists(source) || !File.Exists(sourceMeta))
                throw new PackageException($"{brick.Id} has no asset {asset} (an asset and its .meta are both needed).");

            var target = Unused(Path.Combine(folder, Path.GetFileName(source)));
            var meta = JsonNode.Parse(File.ReadAllText(sourceMeta))!.AsObject();
            var oldId = Guid.Parse((string)meta["Id"]!);
            var newId = Guid.NewGuid();

            File.Copy(source, target);
            File.SetAttributes(target, FileAttributes.Normal);
            meta["RelativePath"] = Path.GetRelativePath(projectRoot, target).Replace('\\', '/');
            File.WriteAllText($"{target}.meta", meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // A data asset's payload repeats its asset id, so the id changes in both files.
            ReplaceId(target, oldId, newId);
            ReplaceId($"{target}.meta", oldId, newId);
            copied.Add(new CopiedAsset(asset, Path.GetRelativePath(projectRoot, target).Replace('\\', '/'), oldId, newId));
        }

        if (remapReferences) Remap(projectRoot, copied);
        return copied;
    }

    static void Remap(string projectRoot, IReadOnlyList<CopiedAsset> copied)
    {
        var own = copied.SelectMany(static c => new[] { Path.GetFullPath(c.Target), Path.GetFullPath($"{c.Target}.meta") }).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(projectRoot, "Assets"), "*", SearchOption.AllDirectories)
                     .Where(file => RemappedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                                    && !own.Contains(Path.GetFullPath(file))))
        {
            var text = File.ReadAllText(file);
            var remapped = copied.Aggregate(text, static (current, c) => current.Replace(c.OldId.ToString(), c.NewId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (remapped != text) File.WriteAllText(file, remapped);
        }
    }

    static void ReplaceId(string path, Guid oldId, Guid newId)
    {
        // Only text files hold ids; a binary asset cannot contain the hyphenated form by accident worth worrying about.
        if (!IsText(path)) return;

        var text = File.ReadAllText(path);
        var replaced = text.Replace(oldId.ToString(), newId.ToString(), StringComparison.OrdinalIgnoreCase);
        if (replaced != text) File.WriteAllText(path, replaced);
    }

    static bool IsText(string path)
    {
        if (path.EndsWith(".meta", StringComparison.Ordinal)) return true;
        var extension = Path.GetExtension(path);
        return RemappedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    static string Unused(string path)
    {
        if (!File.Exists(path) && !File.Exists($"{path}.meta")) return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} {index}{extension}");
            if (!File.Exists(candidate) && !File.Exists($"{candidate}.meta")) return candidate;
        }
    }
}
