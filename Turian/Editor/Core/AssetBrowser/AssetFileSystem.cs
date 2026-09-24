namespace Turian.Editor.Core;

/// <summary>
/// Pure file-system operations for asset management: create, delete, rename,
/// move, duplicate, paste, and unique-path resolution.
/// </summary>
public sealed class AssetFileSystem(SettingsService settingsService, AssetImporter assetImporter)
{
    const string sceneExtension = ".prefab";
    const string materialExtension = ".material";
    const string defaultScene = "New Scene";
    const string defaultMaterial = "New Material";
    const string defaultFolder = "New Folder";

    ClipboardEntry? clipboard;

    /// <summary>
    /// The default folder name to use when creating new folders.
    /// </summary>
    public string DefaultFolderName => defaultFolder;

    /// <summary>
    /// The default scene name to use when creating new scenes.
    /// </summary>
    public string DefaultSceneName => defaultScene;

    /// <summary>The name a newly created material is given.</summary>
    public string DefaultMaterialName => defaultMaterial;

    // ── Clipboard ─────────────────────────────────────────────────────────────

    /// <summary>Copies the path to the internal clipboard.</summary>
    public void Copy(string absolutePath)
    {
        clipboard = new ClipboardEntry(absolutePath, ClipboardOp.Copy);
    }

    /// <summary>Cuts the path to the internal clipboard.</summary>
    public void Cut(string absolutePath)
    {
        clipboard = new ClipboardEntry(absolutePath, ClipboardOp.Cut);
    }

    /// <summary>Returns whether a paste is currently possible.</summary>
    public bool CanPaste() =>
        clipboard is not null
        && (File.Exists(clipboard.SourcePath) || Directory.Exists(clipboard.SourcePath));

    /// <summary>Pastes the clipboard content into <paramref name="targetDirectory"/>.</summary>
    public bool Paste(string targetDirectory)
    {
        if (!CanPaste() || clipboard is null) return false;

        var src = clipboard.SourcePath;
        var isDir = Directory.Exists(src);
        var dest = GetUniquePath(targetDirectory, Path.GetFileName(src), isDir);

        if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            switch (clipboard.Op)
            {
                case ClipboardOp.Copy:
                    if (isDir) DuplicateDirectory(src, dest);
                    else DuplicateAssetWithNewMetadata(src, dest);
                    break;
                case ClipboardOp.Cut:
                    if (isDir) Directory.Move(src, dest);
                    else MoveAssetPreservingMetadata(src, dest);
                    clipboard = null;
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to paste into {Path}", targetDirectory);
            return false;
        }
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Deletes a file or directory at <paramref name="absolutePath"/>.</summary>
    public bool Delete(string absolutePath)
    {
        try
        {
            if (Directory.Exists(absolutePath)) { Directory.Delete(absolutePath, recursive: true); return true; }
            if (File.Exists(absolutePath)) { File.Delete(absolutePath); return true; }
            return false;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to delete {Path}", absolutePath);
            return false;
        }
    }

    /// <summary>
    /// Renames a file or directory. Asset renames preserve the existing GUID.
    /// Returns the destination path on success, <see langword="null"/> on failure.
    /// </summary>
    public string? Rename(string absolutePath, bool isDirectory, string newName)
    {
        if (!TryNormalizeName(newName, isDirectory ? string.Empty : sceneExtension, out var name))
            return null;

        try
        {
            var parent = Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrWhiteSpace(parent)) return null;

            var dest = BuildRenameDestinationPath(parent, absolutePath, name, isDirectory);
            var currentDisplay = isDirectory
                ? Path.GetFileName(absolutePath)
                : Path.GetFileNameWithoutExtension(absolutePath);

            if (string.Equals(currentDisplay, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(absolutePath, dest, StringComparison.OrdinalIgnoreCase))
                return dest;

            if (isDirectory) Directory.Move(absolutePath, dest);
            else RenameAssetPreservingMetadata(absolutePath, dest);

            return dest;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to rename {Path}", absolutePath);
            return null;
        }
    }

    /// <summary>
    /// Updates the in-memory <see cref="Asset.RelativePath"/> after a rename.
    /// Call this after <see cref="Rename"/> succeeds for a file node.
    /// </summary>
    public void UpdateAssetRelativePath(Asset asset, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var projectDir = settingsService.Settings?.ProjectAbsoluteDir;
        if (!string.IsNullOrWhiteSpace(projectDir) && Path.IsPathRooted(destinationPath))
            asset.RenameTo(Path.GetRelativePath(projectDir, destinationPath));
        else
            asset.RenameTo(destinationPath);
    }

    /// <summary>Moves <paramref name="sourcePath"/> into <paramref name="targetDirectory"/>.</summary>
    public bool Move(string sourcePath, string targetDirectory, bool isDirectory)
    {
        try
        {
            var dest = GetUniquePath(targetDirectory, Path.GetFileName(sourcePath), isDirectory);
            if (string.Equals(sourcePath, dest, StringComparison.OrdinalIgnoreCase)) return false;

            if (isDirectory) Directory.Move(sourcePath, dest);
            else MoveAssetPreservingMetadata(sourcePath, dest);
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to move {Source} to {Target}", sourcePath, targetDirectory);
            return false;
        }
    }

    /// <summary>Duplicates a file or directory. Duplicated assets receive a new GUID.</summary>
    public bool Duplicate(string sourcePath, string targetDirectory, bool isDirectory)
    {
        try
        {
            var dest = GetUniquePath(targetDirectory, Path.GetFileName(sourcePath), isDirectory);
            if (isDirectory) DuplicateDirectory(sourcePath, dest);
            else DuplicateAssetWithNewMetadata(sourcePath, dest);
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to duplicate {Path}", sourcePath);
            return false;
        }
    }

    /// <summary>Creates a new empty folder.</summary>
    public bool CreateFolder(string targetDirectory, string folderName)
    {
        if (!TryNormalizeName(folderName, string.Empty, out var name)) return false;
        try
        {
            Directory.CreateDirectory(GetUniquePath(targetDirectory, name, isDirectory: true));
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to create folder in {Path}", targetDirectory);
            return false;
        }
    }

    /// <summary>Creates a new <c>.material</c> asset holding default PBR values.</summary>
    /// <param name="targetDirectory">Absolute path of the directory to create the material in.</param>
    /// <param name="materialName">Name for the new material.</param>
    /// <returns><c>true</c> when the material was written.</returns>
    public async Task<bool> CreateEmptyMaterialAsync(string targetDirectory, string materialName)
    {
        if (!TryNormalizeName(materialName, materialExtension, out var name)) return false;
        try
        {
            var path = GetUniquePath(targetDirectory, name, isDirectory: false);
            var material = new MaterialAsset { Id = Guid.NewGuid(), RelativePath = path };

            // The content file and its meta carry the same id, so the importer adopts the asset
            // the watcher sees rather than minting a second id for it.
            await Serializer.SaveAsync(path, material);
            await Serializer.SaveAsync($"{path}.meta", material);
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to create material in {Path}", targetDirectory);
            return false;
        }
    }

    /// <summary>Creates a new serialized empty scene file.</summary>
    public async Task<bool> CreateEmptySceneAsync(string targetDirectory, string sceneName)
    {
        if (!TryNormalizeName(sceneName, sceneExtension, out var name)) return false;
        try
        {
            var path = GetUniquePath(targetDirectory, name, isDirectory: false);
            var fileName = Path.GetFileNameWithoutExtension(path);
            var root = new Node { Name = string.IsNullOrEmpty(fileName) ? defaultScene : fileName };
            await Serializer.SaveAsync(path, root);
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to create scene in {Path}", targetDirectory);
            return false;
        }
    }

    // ── Tree loading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="rootPath"/> and builds a flat-ish description
    /// as <see cref="AssetEntry"/> records. Consumers map these to their own
    /// node/view types.
    /// </summary>
    public IReadOnlyList<AssetEntry> ScanDirectory(string rootPath)
    {
        var result = new List<AssetEntry>();
        if (!Directory.Exists(rootPath)) return result;
        ScanRecursive(new DirectoryInfo(rootPath), result, parentPath: null);
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns a path that does not yet exist, appending an index if needed.</summary>
    static string GetUniquePath(string directory, string requestedName, bool isDirectory)
    {
        var path = Path.Combine(directory, requestedName);
        if (!PathExists(path)) return path;

        var stem = isDirectory ? requestedName : Path.GetFileNameWithoutExtension(requestedName);
        var ext = isDirectory ? string.Empty : Path.GetExtension(requestedName);
        var i = 1;
        string candidate;
        do { candidate = Path.Combine(directory, $"{stem} {i++}{ext}"); }
        while (PathExists(candidate));
        return candidate;
    }

    /// <summary>
    /// Validates and normalizes a user-supplied file/folder name,
    /// appending <paramref name="requiredExtension"/> if needed.
    /// </summary>
    public static bool TryNormalizeName(string input, string requiredExtension, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(input);
        normalized = input.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (!string.IsNullOrWhiteSpace(requiredExtension)
            && !normalized.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            normalized += requiredExtension;
        return true;
    }

    // ── Private file ops ──────────────────────────────────────────────────────

    static void MoveAssetPreservingMetadata(string src, string dest)
    {
        var srcMeta = $"{src}.meta";
        var destMeta = $"{dest}.meta";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(src, dest);
        if (File.Exists(srcMeta))
        {
            File.Move(srcMeta, destMeta);
            UpdateMetaRelativePath(destMeta, dest);
        }
    }

    static void RenameAssetPreservingMetadata(string src, string dest)
        => MoveAssetPreservingMetadata(src, dest);

    void DuplicateAssetWithNewMetadata(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: false);
        RebuildMetaFileWithNewGuid(dest);
    }

    void DuplicateDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var sub in Directory.GetDirectories(src))
            DuplicateDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
        foreach (var file in Directory.GetFiles(src))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
            RebuildMetaFileWithNewGuid(destFile);
        }
    }

    void RebuildMetaFileWithNewGuid(string assetPath)
    {
        var metaPath = $"{assetPath}.meta";
        if (File.Exists(metaPath)) File.Delete(metaPath);

        var asset = CreateAssetMetadata(assetPath);
        asset.Id = Guid.NewGuid();
        asset.RelativePath = assetPath;
        File.WriteAllText(metaPath, SerializeAssetMetadata(asset));
    }

    Asset CreateAssetMetadata(string assetPath)
    {
        var method = assetImporter.GetType()
            .GetMethod("CreateAssetMetadata", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateAssetMetadata not found on AssetImporter.");
        return method.Invoke(assetImporter, [assetPath]) as Asset
            ?? throw new InvalidOperationException("CreateAssetMetadata returned null.");
    }

    static string SerializeAssetMetadata(Asset asset)
    {
        var method = typeof(AssetImporter)
            .GetMethod("SerializeAssetMetadata", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SerializeAssetMetadata not found on AssetImporter.");
        return method.Invoke(null, [asset]) as string
            ?? throw new InvalidOperationException("SerializeAssetMetadata returned null.");
    }

    static void UpdateMetaRelativePath(string metaPath, string assetPath)
    {
        var asset = Serializer.Load<Asset>(metaPath);
        if (asset is null) return;
        asset.RelativePath = assetPath;
        File.WriteAllText(metaPath, SerializeAssetMetadata(asset));
    }

    static void ScanRecursive(DirectoryInfo dir, List<AssetEntry> result, string? parentPath)
    {
        try
        {
            foreach (var sub in dir.GetDirectories())
            {
                result.Add(new AssetEntry(sub.FullName, IsDirectory: true, AssetMetadata: null, ParentPath: parentPath));
                ScanRecursive(sub, result, sub.FullName);
            }
            foreach (var file in dir.GetFiles())
            {
                if (!file.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                var assetPath = file.FullName[..^".meta".Length];
                if (!File.Exists(assetPath)) continue;

                Asset? meta = null;
                try { meta = Asset.Load(file.FullName); }
                catch (Exception ex) { Log.Logger.LogWarning(ex, "Skipping malformed meta {Path}", file.FullName); }

                result.Add(new AssetEntry(assetPath, IsDirectory: false, AssetMetadata: meta, ParentPath: parentPath));
            }

            foreach (var file in dir.GetFiles("*.cs"))
                if (!File.Exists($"{file.FullName}.meta"))
                    result.Add(new AssetEntry(file.FullName, IsDirectory: false, AssetMetadata: null,
                        ParentPath: parentPath));
        }
        catch (Exception ex) { Log.Logger.LogError(ex, "Error scanning {Path}", dir.FullName); }
    }

    static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    static string BuildRenameDestinationPath(string parent, string src, string name, bool isDir)
    {
        var target = Path.Combine(parent, name);
        if (string.Equals(src, target, StringComparison.OrdinalIgnoreCase)) return target;
        if (!PathExists(target)) return target;
        return GetUniquePath(parent, name, isDir);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    sealed record ClipboardEntry(string SourcePath, ClipboardOp Op);
    enum ClipboardOp { Copy, Cut }
}
