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

    /// <summary>The path the clipboard content came from, or null when the clipboard is empty.</summary>
    public string? ClipboardSource => clipboard?.SourcePath;

    /// <summary>Whether the clipboard content is moved rather than copied when pasted.</summary>
    public bool ClipboardIsCut => clipboard?.Op == ClipboardOp.Cut;

    /// <summary>Pastes the clipboard content into <paramref name="targetDirectory"/>.</summary>
    /// <returns>The pasted entry's path, or null when nothing was pasted.</returns>
    public string? Paste(string targetDirectory)
    {
        if (!CanPaste() || clipboard is null) return null;

        var src = clipboard.SourcePath;
        var isDir = Directory.Exists(src);
        var dest = GetUniquePath(targetDirectory, Path.GetFileName(src), isDir);

        if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) return null;

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
                    return null;
            }
            return dest;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to paste into {Path}", targetDirectory);
            return null;
        }
    }

    /// <summary>
    /// Moves a file or folder into the project's trash instead of deleting it, keeping its meta file and asset id, so
    /// the deletion can be undone.
    /// </summary>
    /// <param name="absolutePath">The file or folder to remove.</param>
    /// <returns>Where it now lies in the trash, or null when it could not be moved.</returns>
    public string? MoveToTrash(string absolutePath)
    {
        if (settingsService.Settings?.ProjectAbsoluteDir is not { } project) return null;

        var isDirectory = Directory.Exists(absolutePath);
        if (!isDirectory && !File.Exists(absolutePath)) return null;

        var trash = Path.Combine(project, ".Cache", "Trash", Guid.NewGuid().ToString("N"),
            Path.GetFileName(absolutePath));
        return MoveTo(absolutePath, trash, isDirectory) ? trash : null;
    }

    /// <summary>Moves a file with its meta file, or a folder, to an exact path, keeping asset ids.</summary>
    /// <param name="sourcePath">The entry to move.</param>
    /// <param name="destinationPath">Its new full path; the parent folder is created when missing.</param>
    /// <param name="isDirectory">Whether the entry is a folder.</param>
    /// <returns>True when the entry moved.</returns>
    public bool MoveTo(string sourcePath, string destinationPath, bool isDirectory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            MoveEntry(sourcePath, destinationPath, isDirectory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to move {Source} to {Destination}", sourcePath, destinationPath);
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

            if (string.Equals(currentDisplay, name, StringComparison.Ordinal)
                || string.Equals(absolutePath, dest, StringComparison.Ordinal))
                return dest;

            // A change of letter case only: where the file system ignores case the target already "exists" as the
            // source itself, so the move goes through a temporary name.
            if (string.Equals(absolutePath, dest, StringComparison.OrdinalIgnoreCase) && PathExists(dest))
            {
                var temporary = Path.Combine(parent, $".rename-{Guid.NewGuid():N}{Path.GetExtension(dest)}");
                MoveEntry(absolutePath, temporary, isDirectory);
                MoveEntry(temporary, dest, isDirectory);
            }
            else
            {
                MoveEntry(absolutePath, dest, isDirectory);
            }

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
    /// <returns>The copy's path, or null when it could not be made.</returns>
    public string? Duplicate(string sourcePath, string targetDirectory, bool isDirectory)
    {
        try
        {
            var dest = GetUniquePath(targetDirectory, Path.GetFileName(sourcePath), isDirectory);
            if (isDirectory) DuplicateDirectory(sourcePath, dest);
            else DuplicateAssetWithNewMetadata(sourcePath, dest);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to duplicate {Path}", sourcePath);
            return null;
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

    static void MoveEntry(string src, string dest, bool isDirectory)
    {
        if (isDirectory) Directory.Move(src, dest);
        else RenameAssetPreservingMetadata(src, dest);
    }

    // The copy is the project's own: writable even when the source is a read-only brick file, with the source's
    // import settings under a new asset id that is also rewritten inside the copy's own payload.
    void DuplicateAssetWithNewMetadata(string src, string dest, List<(string Path, Guid Old, Guid New)>? copied = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: false);
        File.SetAttributes(dest, FileAttributes.Normal);

        if (!TryCloneMeta($"{src}.meta", dest, copied)) RebuildMetaFileWithNewGuid(dest);
    }

    static bool TryCloneMeta(string sourceMeta, string dest, List<(string Path, Guid Old, Guid New)>? copied)
    {
        if (!File.Exists(sourceMeta)) return false;

        var text = File.ReadAllText(sourceMeta);
        if (!Guid.TryParse(JsonNode.Parse(text)?["Id"]?.GetValue<string>(), out var oldId)) return false;

        var newId = Guid.NewGuid();
        copied?.Add((dest, oldId, newId));
        var destMeta = $"{dest}.meta";
        File.WriteAllText(destMeta, text.Replace(oldId.ToString(), newId.ToString(), StringComparison.OrdinalIgnoreCase));
        BrickAssetCopy.ReplaceId(dest, oldId, newId);
        UpdateMetaRelativePath(destMeta, dest);
        return true;
    }

    // Assets copied together point at each other's copies, so a copied prefab keeps using the copied material.
    void DuplicateDirectory(string src, string dest)
    {
        var copied = new List<(string Path, Guid Old, Guid New)>();
        DuplicateDirectory(src, dest, copied);

        var ids = copied.Select(static c => (c.Old, c.New)).ToList();
        foreach (var (path, _, _) in copied) BrickAssetCopy.ReplaceIds(path, ids);
    }

    void DuplicateDirectory(string src, string dest, List<(string Path, Guid Old, Guid New)> copied)
    {
        Directory.CreateDirectory(dest);
        foreach (var sub in Directory.GetDirectories(src).Where(static d => !Path.GetFileName(d).EndsWith('~')))
            DuplicateDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)), copied);
        foreach (var file in Directory.GetFiles(src))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            DuplicateAssetWithNewMetadata(file, destFile, copied);
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
