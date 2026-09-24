namespace Turian.Editor.Core;

/// <summary>
/// Options controlling how <see cref="OapArchiveBuilder"/> packs a project.
/// </summary>
public sealed class OapPackOptions
{
    /// <summary>Gets or sets the package name written into the manifest. Defaults to the project folder name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the package version string written into the manifest.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Gets or sets how each asset is compressed.</summary>
    public OapCompressChoice Compression { get; set; } = OapCompressChoice.Auto;

    /// <summary>
    /// Gets or sets a passphrase; when set, every asset is encrypted with ChaCha20 and the
    /// key is never stored in the package.
    /// </summary>
    public string? Passphrase { get; set; }

    /// <summary>
    /// Gets or sets the build target whose artifact to pack for assets that bake one per
    /// target (textures). <see langword="null"/> packs each asset's primary artifact.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>Gets the names of other packages this one requires, written into the manifest.</summary>
    public IList<string> Requires { get; } = [];
}

/// <summary>
/// Result of building an Open Asset Package.
/// </summary>
/// <param name="OapFilePath">The generated <c>.oap</c> file path.</param>
/// <param name="CatalogFilePath">The generated runtime catalog path, or empty when none was written.</param>
/// <param name="EntryCount">The number of packed assets.</param>
public sealed record OapArchiveBuildResult(string OapFilePath, string CatalogFilePath, int EntryCount);

/// <summary>
/// Builds an Open Asset Package, and optionally a matching runtime catalog, from a
/// project's imported asset cache.
/// </summary>
public sealed class OapArchiveBuilder(ILogger logger)
{
    const string cacheDirectoryName = ".Cache";
    const string cacheCatalogFileName = "assetCatalog.json";
    const string contentDirectoryName = "Content";
    const string runtimeCatalogFileName = "assetCatalog.json";
    const string generatorName = "Turian";

    readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Packs every imported asset into <c>&lt;output&gt;/Content/&lt;name&gt;.oap</c> and writes a
    /// runtime catalog beside it. Used by Export.
    /// </summary>
    /// <param name="projectRootPath">Absolute project root path.</param>
    /// <param name="outputRootPath">Absolute output directory.</param>
    /// <param name="options">Packing options, or <see langword="null"/> for defaults.</param>
    /// <returns>The build result.</returns>
    public OapArchiveBuildResult Build(string projectRootPath, string outputRootPath, OapPackOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootPath);
        options ??= new OapPackOptions();

        var outputRoot = Path.GetFullPath(outputRootPath);
        var projectRoot = Path.GetFullPath(projectRootPath);
        var packageName = ResolvePackageName(options.Name, projectRoot);

        var contentDirectory = Path.Combine(outputRoot, contentDirectoryName);
        Directory.CreateDirectory(contentDirectory);

        var oapFilePath = Path.Combine(contentDirectory, packageName + OapFormat.FileExtension);
        var runtimeCatalogPath = Path.Combine(contentDirectory, runtimeCatalogFileName);

        var packed = Pack(projectRoot, oapFilePath, packageName, options);

        var oapRelativePath = Path.Combine(contentDirectoryName, packageName + OapFormat.FileExtension);
        Serializer.Save(runtimeCatalogPath, CreateRuntimeCatalog(packed, outputRoot, oapRelativePath));

        logger.LogInformation(
            "OAP package created with {EntryCount} asset(s) at {OapFilePath}",
            packed.Count,
            oapFilePath);

        return new OapArchiveBuildResult(oapFilePath, runtimeCatalogPath, packed.Count);
    }

    /// <summary>
    /// Packs every imported asset into a single <c>.oap</c> file at an explicit path,
    /// without the <c>Content/</c> layout and without a runtime catalog. Used by the CLI.
    /// </summary>
    /// <param name="projectRootPath">Absolute project root path.</param>
    /// <param name="oapFilePath">Absolute destination <c>.oap</c> path.</param>
    /// <param name="options">Packing options, or <see langword="null"/> for defaults.</param>
    /// <returns>The build result, with an empty catalog path.</returns>
    public OapArchiveBuildResult BuildPackage(string projectRootPath, string oapFilePath, OapPackOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oapFilePath);
        options ??= new OapPackOptions();

        var projectRoot = Path.GetFullPath(projectRootPath);
        var target = Path.GetFullPath(oapFilePath);
        var packageName = ResolvePackageName(
            options.Name ?? Path.GetFileNameWithoutExtension(target),
            projectRoot);

        var packed = Pack(projectRoot, target, packageName, options);

        logger.LogInformation(
            "OAP package created with {EntryCount} asset(s) at {OapFilePath}",
            packed.Count,
            target);

        return new OapArchiveBuildResult(target, string.Empty, packed.Count);
    }

    List<AssetRecord> Pack(string projectRoot, string oapFilePath, string packageName, OapPackOptions options)
    {
        if (!Directory.Exists(projectRoot))
        {
            throw new DirectoryNotFoundException($"Project root was not found: {projectRoot}");
        }

        var sourceCatalogPath = Path.Combine(projectRoot, cacheDirectoryName, cacheCatalogFileName);
        if (!File.Exists(sourceCatalogPath))
        {
            throw new FileNotFoundException(
                "Asset cache catalog was not found. Import assets before packing.",
                sourceCatalogPath);
        }

        var sourceCatalog = Serializer.Load<AssetCatalog>(sourceCatalogPath) ?? new AssetCatalog();

        var writer = new OapWriter();
        var encryption = OapEncryption.None;
        if (!string.IsNullOrEmpty(options.Passphrase))
        {
            writer.SetKey(OapCrypto.DeriveKey(options.Passphrase));
            encryption = OapEncryption.ChaCha20;
        }

        var childrenByParent = sourceCatalog.Records
            .Where(static r => r.ParentAssetId != Guid.Empty)
            .GroupBy(static r => r.ParentAssetId)
            .ToDictionary(static g => g.Key, static g => g.Select(static r => r.AssetId).ToArray());

        var packed = new List<AssetRecord>();
        foreach (var record in sourceCatalog.Records.OrderBy(static r => r.AssetId))
        {
            if (record.AssetId == Guid.Empty || IsSourceCodeRecord(record))
            {
                continue;
            }

            var payloadPath = ResolvePayloadPath(record, projectRoot, options.Target);
            if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            {
                logger.LogWarning(
                    "Skipping asset {AssetId}: imported payload could not be resolved ({ImportedRelativePath})",
                    record.AssetId,
                    record.ImportedRelativePath);
                continue;
            }

            var deps = childrenByParent.TryGetValue(record.AssetId, out var children) ? children : [];

            writer.Add(
                record.AssetId,
                File.ReadAllBytes(payloadPath),
                virtualPath: NormalizeVirtualPath(record.ImportedRelativePath),
                assetType: (byte)ClassifyAssetType(record.AssetTypeName, payloadPath),
                compression: options.Compression,
                encryption: encryption,
                dependencies: deps);

            packed.Add(record);
        }

        writer.SetManifest(BuildManifest(packageName, options));
        writer.WriteToFile(oapFilePath);
        return packed;
    }

    static string ResolvePackageName(string? requested, string projectRoot)
    {
        var name = string.IsNullOrWhiteSpace(requested)
            ? new DirectoryInfo(projectRoot).Name
            : requested;

        var cleaned = new string([.. name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? "assets" : cleaned;
    }

    static bool IsSourceCodeRecord(AssetRecord record)
    {
        var path = string.IsNullOrWhiteSpace(record.SourceRelativePath)
            ? record.ImportedRelativePath
            : record.SourceRelativePath;

        return !string.IsNullOrWhiteSpace(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    static string ResolvePayloadPath(AssetRecord record, string projectRoot, string? target)
    {
        if (target is not null &&
            record.TargetArtifacts.TryGetValue(target, out var targetArtifact) &&
            !string.IsNullOrWhiteSpace(targetArtifact))
        {
            return Rooted(targetArtifact, projectRoot);
        }

        if (!string.IsNullOrWhiteSpace(record.ImportedRelativePath))
        {
            return Rooted(record.ImportedRelativePath, projectRoot);
        }

        return string.IsNullOrWhiteSpace(record.SourceRelativePath)
            ? string.Empty
            : Rooted(record.SourceRelativePath, projectRoot);
    }

    static string Rooted(string path, string root) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));

    static string NormalizeVirtualPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/');

    static OapAssetType ClassifyAssetType(string assetTypeName, string payloadPath)
    {
        var name = assetTypeName;
        if (name.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
            payloadPath.EndsWith(".amtex", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Texture;
        }

        if (name.Contains("Mesh", StringComparison.OrdinalIgnoreCase) ||
            payloadPath.EndsWith(".ammesh", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Mesh;
        }

        if (name.Contains("Material", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Material;
        }

        if (name.Contains("Prefab", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Prefab;
        }

        if (name.Contains("Scene", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Scene;
        }

        if (name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Sound", StringComparison.OrdinalIgnoreCase))
        {
            return OapAssetType.Audio;
        }

        return string.IsNullOrWhiteSpace(name) ? OapAssetType.Unknown : OapAssetType.Other;
    }

    static byte[] BuildManifest(string name, OapPackOptions options)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("name", name);
            json.WriteString("version", options.Version);
            json.WriteString("generator", $"{generatorName} {typeof(OapArchiveBuilder).Assembly.GetName().Version}");
            json.WriteStartArray("requires");
            foreach (var required in options.Requires)
            {
                json.WriteStringValue(required);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    static AssetCatalog CreateRuntimeCatalog(
        IReadOnlyList<AssetRecord> packed,
        string outputRoot,
        string oapRelativePath)
    {
        var catalog = new AssetCatalog
        {
            Version = AssetCatalog.CurrentVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        foreach (var source in packed)
        {
            catalog.Records.Add(new AssetRecord
            {
                AssetId = source.AssetId,
                ProjectRootPath = outputRoot,
                AssetTypeName = source.AssetTypeName,
                ParentAssetId = source.ParentAssetId,
                SourceRelativePath = source.SourceRelativePath,
                MetaRelativePath = source.MetaRelativePath,
                PrimaryContentKey = string.IsNullOrWhiteSpace(source.PrimaryContentKey)
                    ? AssetRecord.CreatePrimaryContentKey(source.AssetId)
                    : source.PrimaryContentKey,
                ImportedRelativePath = oapRelativePath,
                StorageKind = AssetStorageKind.Oap,
                SourceHash = source.SourceHash,
                SettingsHash = source.SettingsHash,
                ImporterId = source.ImporterId,
                ImporterVersion = source.ImporterVersion,
                Artifacts = [oapRelativePath],
                ComponentTypes = [.. source.ComponentTypes]
            });
        }

        return catalog;
    }
}
