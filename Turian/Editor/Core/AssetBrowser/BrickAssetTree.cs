using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>Builds the browser's brick tree from resolved sources, including bricks outside the project.</summary>
public sealed class BrickAssetTree(AssetFileSystem files)
{
    /// <summary>Scans a project's resolved bricks; unresolved projects retain an empty brick root.</summary>
    /// <param name="projectRoot">The installing project.</param>
    /// <returns>The brick tree entries.</returns>
    public IReadOnlyList<AssetEntry> Scan(string projectRoot)
    {
        try
        {
            return Scan(projectRoot, ProjectPackages.ResolveOrEmpty(projectRoot));
        }
        catch (PackageException ex)
        {
            Log.Logger.LogWarning(ex, "Could not browse the project's bricks");
            return Scan(projectRoot, []);
        }
    }

    /// <summary>Scans importable brick content, marking installed sources as read-only.</summary>
    /// <param name="projectRoot">The installing project.</param>
    /// <param name="bricks">The resolved bricks.</param>
    /// <returns>A flat tree rooted at the project's Packages directory.</returns>
    public IReadOnlyList<AssetEntry> Scan(string projectRoot, IReadOnlyList<ResolvedPackage> bricks)
    {
        var root = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var entries = new List<AssetEntry> { new(root, true, null, null, IsReadOnly: true) };
        var assetsRoot = Path.Combine(projectRoot, "Assets") + Path.DirectorySeparatorChar;
        foreach (var brick in bricks)
        {
            // An authored brick under Assets is already present in the project's tree.
            if (brick.RootPath.StartsWith(assetsRoot, StringComparison.Ordinal)) continue;
            var readOnly = brick.IsReadOnly;
            entries.Add(new AssetEntry(brick.RootPath, true, null, root, readOnly));
            entries.AddRange(files.ScanDirectory(brick.RootPath)
                .Where(entry => !Path.GetRelativePath(brick.RootPath, entry.AbsolutePath)
                    .Split(Path.DirectorySeparatorChar).Any(part => part.EndsWith('~')))
                .Select(entry => entry with { ParentPath = entry.ParentPath ?? brick.RootPath, IsReadOnly = readOnly }));
        }
        return entries;
    }
}
