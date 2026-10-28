namespace Turian.Editor.Core;

/// <summary>One entry in the asset browser's Create menu.</summary>
/// <param name="MenuPath">Slash-separated path, e.g. <c>Gameplay/Puzzle Data</c>.</param>
/// <param name="DefaultName">The name a freshly created one gets.</param>
/// <param name="PayloadType">The <see cref="DataAsset"/> type to write, or null for a built-in kind.</param>
public sealed record AssetCreationKind(string MenuPath, string DefaultName, Type? PayloadType = null);

/// <summary>
/// What the asset browser can create: built-in assets and constructible
/// <see cref="DataAsset"/> types carrying <see cref="CreateAssetMenuAttribute"/>.
/// Discovery follows the active project assemblies and loaded editor bricks.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetCreationCatalog : IDisposable
{
    const string dataAssetExtension = ".dataasset";

    readonly AssetFileSystem fileSystem;
    readonly SettingsService settings;
    readonly BuildManager buildManager;
    readonly ILogger log;
    readonly IReadOnlyList<IAssetCreationProvider> providers;

    readonly List<AssetCreationKind> kinds = [];
    ReflectionAssembly[] scanned = [];
    bool scannedOnce;

    /// <summary>Creates the catalog over the services that write the files.</summary>
    /// <param name="fileSystem">Creates the built-in kinds.</param>
    /// <param name="settings">Resolves the project directory a data asset's path is relative to.</param>
    /// <param name="buildManager">Supplies the user assembly the annotated types come from.</param>
    /// <param name="log">Where a failed creation is reported.</param>
    /// <param name="providers">Asset creation entries contributed by editor bricks.</param>
    public AssetCreationCatalog(AssetFileSystem fileSystem, SettingsService settings,
        BuildManager buildManager, ILogger log, IEnumerable<IAssetCreationProvider>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(buildManager);
        ArgumentNullException.ThrowIfNull(log);

        this.fileSystem = fileSystem;
        this.settings = settings;
        this.buildManager = buildManager;
        this.log = log;
        this.providers = providers?.ToArray() ?? [];
        buildManager.UserAssembliesChanged += Invalidate;
    }

    /// <summary>The kinds currently offered, built-ins first.</summary>
    public IReadOnlyList<AssetCreationKind> Kinds
    {
        get
        {
            EnsureRefreshed();
            return kinds;
        }
    }

    /// <summary>Rescans when the eligible assemblies change, including newly loaded bricks.</summary>
    public void EnsureRefreshed()
    {
        var current = CandidateAssemblies(buildManager.LoadedAssemblies, buildManager.ActiveUserAssemblies).ToArray();
        if (scannedOnce && current.SequenceEqual(scanned))
        {
            return;
        }

        scanned = current;
        scannedOnce = true;

        kinds.Clear();
        kinds.Add(new AssetCreationKind("Folder", fileSystem.DefaultFolderName));
        kinds.Add(new AssetCreationKind("Scene", fileSystem.DefaultSceneName));
        kinds.Add(new AssetCreationKind("Material", fileSystem.DefaultMaterialName));
        // Providers outlive brick changes until restart, so their kinds follow the same enabled-brick filter.
        kinds.AddRange(Scan(current, log)
            .Concat(providers.SelectMany(static provider => provider.Kinds)
                .Where(static kind => kind.PayloadType is null || !BrickAssemblies.IsRetired(kind.PayloadType.Assembly)))
            .DistinctBy(static kind => kind.PayloadType));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        buildManager.UserAssembliesChanged -= Invalidate;
        Invalidate();
    }

    void Invalidate()
    {
        scanned = [];
        scannedOnce = false;
        kinds.Clear();
    }

    /// <summary>
    /// Creates one asset of <paramref name="kind"/> in <paramref name="targetDirectory"/>. Blocking:
    /// the underlying writers are async, and the asset browser drives this from its render loop.
    /// </summary>
    /// <param name="kind">What to create.</param>
    /// <param name="targetDirectory">Absolute directory to create it in.</param>
    /// <returns>True when the asset was written.</returns>
    public bool Create(AssetCreationKind kind, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        try
        {
            if (kind.PayloadType is not null)
            {
                return CreateDataAsset(kind, targetDirectory);
            }

            return kind.MenuPath switch
            {
                "Folder" => fileSystem.CreateFolder(targetDirectory, kind.DefaultName),
                "Scene" => fileSystem.CreateEmptySceneAsync(targetDirectory, kind.DefaultName)
                    .GetAwaiter().GetResult(),
                "Material" => fileSystem.CreateEmptyMaterialAsync(targetDirectory, kind.DefaultName)
                    .GetAwaiter().GetResult(),
                _ => false
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not create a {Kind} in {Directory}", kind.MenuPath, targetDirectory);
            return false;
        }
    }

    bool CreateDataAsset(AssetCreationKind kind, string targetDirectory)
    {
        if (settings.Settings is not { } appSettings)
        {
            return false;
        }

        if (Activator.CreateInstance(kind.PayloadType!) is not DataAsset payload)
        {
            return false;
        }

        var assetPath = UniquePath(targetDirectory, kind.DefaultName);
        if (payload is EnumValueAsset named) { named.Name = Path.GetFileNameWithoutExtension(assetPath); }
        var meta = new DataAssetAsset
        {
            RelativePath = Path.GetRelativePath(appSettings.ProjectAbsoluteDir, assetPath)
        };
        payload.Id = meta.Id;

        Directory.CreateDirectory(targetDirectory);
        Serializer.SaveAsync(assetPath, payload).GetAwaiter().GetResult();
        Serializer.SaveAsync(assetPath + ".meta", meta).GetAwaiter().GetResult();
        ProjectSettingsFiles.RegisterCreated(appSettings, payload, meta.Id);
        return true;
    }

    /// <summary>
    /// The assemblies worth scanning: the engine and whatever references it, with every user assembly but
    /// the active ones left out. The editor process also holds MSBuild and Roslyn, whose types can fail to
    /// resolve their base types, and each recompile leaves the previous user assemblies loaded.
    /// </summary>
    /// <param name="loaded">Every assembly the editor has loaded.</param>
    /// <param name="activeUserAssemblies">The user assemblies in force, empty before the first compile.</param>
    /// <returns>The assemblies to scan.</returns>
    public static IEnumerable<ReflectionAssembly> CandidateAssemblies(IEnumerable<ReflectionAssembly> loaded,
        IReadOnlyCollection<ReflectionAssembly> activeUserAssemblies)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(activeUserAssemblies);

        var engine = typeof(DataAsset).Assembly;
        var engineName = engine.GetName().Name;
        var attributesName = typeof(CreateAssetMenuAttribute).Assembly.GetName().Name;

        return loaded
            .Where(static assembly => !assembly.IsDynamic)
            .Where(assembly => !assembly.IsCollectible || activeUserAssemblies.Contains(assembly))
            .Where(assembly => assembly == engine
                               || assembly.GetReferencedAssemblies().Any(reference => reference.Name == engineName
                                   || reference.Name == attributesName))
            .Distinct();
    }

    /// <summary>
    /// Every <see cref="DataAsset"/> carrying <see cref="CreateAssetMenuAttribute"/> in the given assemblies.
    /// A type that cannot be inspected is skipped rather than ending the scan.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <param name="log">Where a skipped type is reported.</param>
    /// <returns>The kinds, ordered by menu path.</returns>
    public static IReadOnlyList<AssetCreationKind> Scan(IEnumerable<ReflectionAssembly> assemblies, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(log);

        var found = assemblies.SelectMany(LoadableTypes)
            .Select(type => CreationKind(type, log)).OfType<AssetCreationKind>();
        return [.. found.OrderBy(static kind => kind.MenuPath, StringComparer.OrdinalIgnoreCase)];
    }

    static bool IsCreatableDataAsset(Type type) =>
        type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
        && typeof(DataAsset).IsAssignableFrom(type) && !typeof(Asset).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;

    static AssetCreationKind? CreationKind(Type type, ILogger log)
    {
        try
        {
            if (IsCreatableDataAsset(type)
                && type.GetCustomAttribute<CreateAssetMenuAttribute>(false) is { } attribute)
            {
                return new AssetCreationKind(MenuPath(attribute.Path, type), FileName(attribute.FileName, type), type);
            }
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException
                                       or BadImageFormatException)
        {
            log.LogDebug(ex, "New menu: skipped {Type}", type.FullName);
        }
        return null;
    }

    static IEnumerable<Type> LoadableTypes(ReflectionAssembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or NotSupportedException)
        {
            return [];
        }
    }

    static string MenuPath(string? configured, Type payloadType) =>
        string.IsNullOrWhiteSpace(configured) ? payloadType.Name : configured.Trim();

    static string FileName(string? configured, Type payloadType)
    {
        var candidate = string.IsNullOrWhiteSpace(configured) ? payloadType.Name : configured.Trim();

        return candidate.EndsWith(dataAssetExtension, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : candidate + dataAssetExtension;
    }

    static string UniquePath(string directory, string requestedName)
    {
        var path = Path.Combine(directory, requestedName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = Path.GetExtension(requestedName);

        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
