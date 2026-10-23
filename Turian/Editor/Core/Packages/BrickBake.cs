using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>One baked asset of a brick, as <c>bake-sync.json</c> records it.</summary>
public sealed class BakeSyncEntry
{
    /// <summary>The asset's path in the brick folder, with <c>/</c> separators.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The asset's id, from its committed meta file.</summary>
    public Guid Guid { get; set; }

    /// <summary>The source file's hash, <c>sha256-</c> + base64.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The importer that produced the artifacts.</summary>
    public string ImporterId { get; set; } = string.Empty;

    /// <summary>The importer's own version.</summary>
    public int ImporterVersion { get; set; }

    /// <summary>The import pipeline's version.</summary>
    public int PipelineVersion { get; set; }

    /// <summary>The hash of the asset's import settings.</summary>
    public string SettingsHash { get; set; } = string.Empty;

    /// <summary>The baked files, relative to the bake folder, with their hashes.</summary>
    public List<BakeSyncArtifact> Artifacts { get; set; } = [];
}

/// <summary>One baked file.</summary>
/// <param name="File">Its path, relative to the bake folder.</param>
/// <param name="Hash">Its hash, <c>sha256-</c> + base64.</param>
public sealed record BakeSyncArtifact(string File, string Hash);

/// <summary>A brick's <c>bake-sync.json</c>: what its bake folder holds, so a twin can be checked asset by asset.</summary>
public sealed class BakeSync
{
    /// <summary>The brick baked.</summary>
    public string Brick { get; set; } = string.Empty;

    /// <summary>Its version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The fingerprint of the key that will publish the twin, when known.</summary>
    public string? Baker { get; set; }

    /// <summary>The engine version that baked it.</summary>
    public string BakedWith { get; set; } = string.Empty;

    /// <summary>The baked assets, by path.</summary>
    public List<BakeSyncEntry> Entries { get; set; } = [];
}

/// <summary>
/// Bakes a brick: imports its assets headlessly in a scratch project and keeps each import result in the brick's
/// bake folder (the v2 <c>bake</c> role, or <see cref="Folder"/>). A project installing the brick's twin mounts those results instead of importing, as long as the
/// source file, its import settings and the importer's version still match; otherwise it imports and warns.
/// </summary>
public static class BrickBake
{
    /// <summary>The folder, at a v1 brick's root, holding its baked imports; v2 bricks name it by the <c>bake</c> role.</summary>
    public const string Folder = "Bake~";

    /// <summary>The coverage record inside the bake folder.</summary>
    public const string SyncFileName = "bake-sync.json";

    /// <summary>The folder inside the bake folder holding one import directory per asset id.</summary>
    public const string ImportedFolder = "Imported";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The baked import of an asset inside a brick's bake folder, when the brick carries one.</summary>
    /// <param name="bakeFolder">The brick's bake folder, from <see cref="BrickLayout.PathOf"/>.</param>
    /// <param name="assetId">The asset id.</param>
    /// <returns>The import directory, or null.</returns>
    public static string? BakedImportOf(string bakeFolder, Guid assetId)
    {
        var directory = Path.Combine(bakeFolder, ImportedFolder, assetId.ToString("N"));
        return Directory.Exists(directory) ? directory : null;
    }

    /// <summary>Bakes the brick at <paramref name="packageRoot"/> into its bake folder, replacing any earlier bake.</summary>
    /// <param name="packageRoot">The brick folder; its assets must carry committed meta files.</param>
    /// <param name="logger">Receives import progress.</param>
    /// <param name="baker">The fingerprint of the key that will publish the twin, recorded when given.</param>
    /// <returns>What was baked.</returns>
    /// <exception cref="PackageException">The brick fails verification or its scratch project cannot be made.</exception>
    public static async Task<BakeSync> BakeAsync(string packageRoot, ILogger logger, string? baker = null)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        if (BrickVerifier.Verify(packageRoot) is { Count: > 0 } issues)
        {
            throw new PackageException($"{packageRoot} cannot be baked:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", issues)}");
        }

        var manifest = PackageManifest.Load(packageRoot, ["gaya", ProjectPackages.HostName]);
        var bake = BrickLayout.PathOf(packageRoot, manifest, BrickLayout.Bake);
        if (BrickLayout.IsV2(manifest) && manifest.RolePaths(BrickLayout.Bake).Count == 0)
        {
            manifest.Roles[BrickLayout.Bake] = JsonSerializer.SerializeToElement(BrickLayout.RelativePathOf(manifest, BrickLayout.Bake));
            manifest.Save(packageRoot);
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"turian-bake-{Guid.NewGuid():N}");
        try
        {
            // Baking a copy keeps the import from touching the brick's own files.
            var copy = Path.Combine(scratch, "brick");
            var bakes = BrickLayout.PathsOf(packageRoot, manifest, BrickLayout.Bake);
            CopyDirectory(packageRoot, copy, skip: directory => bakes.Contains(directory) || Path.GetFileName(directory) is ".git" or "bin" or "obj");
            var project = Path.Combine(scratch, "project");
            _ = await new ProjectBootstrapper().CreateAsync(project).ConfigureAwait(false)
                ?? throw new PackageException("The scratch project for the bake could not be created.");
            ProjectBricks.Add(project, manifest.Name, $"file:{copy}");
            ProjectPackages.Invalidate(project);
            var resolution = ProjectPackages.Resolve(project);
            BrickAssemblies.Load(resolution.Packages, logger, project);

            var appSettings = SettingsService.Load(project) ?? throw new PackageException("The scratch project has no settings.");
            ProjectSettingsLoader.LoadFromSources(appSettings);
            var settingsService = new SettingsService();
            settingsService.Set(appSettings);
            using (var importer = new AssetImporter(logger, new AssetDatabase(), settingsService))
            {
                importer.GenerateMetaFiles(appSettings.AssetsAbsoluteDir);
            }

            var sync = Collect(copy, project, bake);
            sync.Brick = manifest.Name;
            sync.Version = manifest.Version!.ToString();
            sync.Baker = baker;
            sync.BakedWith = ProjectPackages.EngineVersion.ToString();
            File.WriteAllText(Path.Combine(bake, SyncFileName), JsonSerializer.Serialize(sync, JsonOptions));
            logger.LogInformation("Baked {Count} asset(s) of {Brick} into {Folder}", sync.Entries.Count, manifest.Name, bake);
            return sync;
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>Copies the scratch project's import of every asset of the brick copy into the brick's bake folder.</summary>
    static BakeSync Collect(string copy, string project, string bake)
    {
        if (Directory.Exists(bake))
        {
            Directory.Delete(bake, recursive: true);
        }

        Directory.CreateDirectory(Path.Combine(bake, ImportedFolder));
        var sync = new BakeSync();
        foreach (var meta in Directory.EnumerateFiles(copy, "*.meta", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(copy, meta[..^".meta".Length]).Replace('\\', '/');
            if (BrickVerifier.IsUnimported(relative) || relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || Asset.Load(meta) is not { } asset)
            {
                continue;
            }

            var imported = AssetImporter.ImportDirectoryOf(project, asset.Id);
            var manifestPath = Path.Combine(imported, AssetImporter.ImportManifestFileName);
            if (!File.Exists(manifestPath) || Serializer.Load<ImportedAssetManifest>(manifestPath) is not { } import)
            {
                throw new PackageException($"The bake has no successful import of {relative}; fix the import errors before packing a twin.");
            }

            var target = Path.Combine(bake, ImportedFolder, asset.Id.ToString("N"));
            CopyDirectory(imported, target, skip: static _ => false);

            // Machine-specific stamps would make every bake differ; the mounting project writes its own.
            import.SourceRelativePath = relative;
            import.MetaRelativePath = relative + ".meta";
            import.SourceLastWriteTimeUtc = default;
            import.ImportedAtUtc = default;
            Serializer.Save(Path.Combine(target, AssetImporter.ImportManifestFileName), import);

            sync.Entries.Add(new BakeSyncEntry
            {
                Path = relative,
                Guid = asset.Id,
                ContentHash = Hash(Path.Combine(copy, relative)),
                ImporterId = import.ImporterId,
                ImporterVersion = import.ImporterVersion,
                PipelineVersion = import.PipelineVersion,
                SettingsHash = import.SettingsHash,
                Artifacts = [.. Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                    .Select(file => new BakeSyncArtifact(Path.GetRelativePath(bake, file).Replace('\\', '/'), Hash(file)))],
            });
        }

        return sync;
    }

    static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return "sha256-" + Convert.ToBase64String(SHA256.HashData(stream));
    }

    /// <summary>Copies a folder, leaving out the subfolders <paramref name="skip"/> accepts by full path.</summary>
    static void CopyDirectory(string from, string to, Func<string, bool> skip)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(from).Where(d => !skip(d)))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)), skip);
        }
    }
}
