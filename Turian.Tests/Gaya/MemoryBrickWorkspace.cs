namespace Turian.Tests;

/// <summary>
/// A project workspace held in memory for Bricks panel tests: its registries list a fixed catalog without a network,
/// enabling a brick writes a fixture folder, and it counts how many times the project was brought in line, which is
/// when Turian recompiles.
/// </summary>
sealed class MemoryBrickWorkspace : IBrickWorkspace, IProjectBricks
{
    readonly Lock sync = new();
    readonly Dictionary<string, string> used = new(StringComparer.Ordinal);
    readonly string fixtures;
    int applied;

    /// <summary>Creates the workspace in <paramref name="root"/>.</summary>
    /// <param name="root">A scratch folder.</param>
    public MemoryBrickWorkspace(string root)
    {
        Root = Path.Combine(root, "project");
        fixtures = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(Path.Combine(Root, ProjectManifest.DirectoryName));
        Directory.CreateDirectory(fixtures);
    }

    /// <summary>What the registries list.</summary>
    public RegistryCatalog Listings { get; set; } = new([], []);

    /// <summary>Completes when a restore ran, as the panel's first registry refresh ends with.</summary>
    public TaskCompletionSource Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many times the project was brought in line after its bricks changed.</summary>
    public int Applied => Volatile.Read(ref applied);

    /// <summary>The ids in use.</summary>
    public IReadOnlyList<string> Used
    {
        get
        {
            lock (sync)
            {
                return [.. used.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public IBrickWorkspace? Workspace => this;

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public PackageScope Scope => PackageScope.Project;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReservedCategoryPrefixes { get; } = ["gaya", ProjectPackages.HostName];

    /// <inheritdoc />
    public string? BuiltinDirectory => null;

    /// <inheritdoc />
    public ScopedRegistry? PublicRegistry => null;

    /// <inheritdoc />
    public bool SupportsForks => false;

    /// <inheritdoc />
    public IReadOnlyList<ResolvedPackage> Resolve()
    {
        lock (sync)
        {
            return
            [
                .. used.Select(pair => new ResolvedPackage(pair.Key, PackageManifest.Load(pair.Value, ReservedCategoryPrefixes),
                    pair.Value, PackageOrigin.Registry, "registry:bricks.example", null, null, 1, false)),
            ];
        }
    }

    /// <inheritdoc />
    public void Add(string id, string? source)
    {
        if (source?.Contains("nowhere", StringComparison.Ordinal) == true)
        {
            throw new PackageException($"{id}: {source} cannot be found.");
        }

        var folder = Path.Combine(fixtures, id);
        if (!Directory.Exists(folder))
        {
            _ = BrickService.New(fixtures, id);
        }

        lock (sync)
        {
            used[id] = folder;
        }
    }

    /// <inheritdoc />
    public void Disable(string id)
    {
        lock (sync)
        {
            if (!used.Remove(id))
            {
                throw new PackageException($"The project does not declare {id}.");
            }
        }
    }

    /// <inheritdoc />
    public void Restore() => Restored.TrySetResult();

    /// <inheritdoc />
    public void Changed(bool applied)
    {
        if (applied)
        {
            _ = Interlocked.Increment(ref this.applied);
        }
    }

    /// <inheritdoc />
    public Task<RegistryCatalog> ListAsync() => Task.FromResult(Listings);

    /// <inheritdoc />
    public Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string query) =>
        Task.FromResult<IReadOnlyList<(string, string, string)>>([]);

    /// <inheritdoc />
    public IReadOnlyList<ScopedRegistry> Registries() => [];

    /// <inheritdoc />
    public void Update(IReadOnlyCollection<string> ids)
    {
    }

    /// <inheritdoc />
    public bool IsPrecast(ResolvedPackage brick) => false;

    /// <inheritdoc />
    public string AddFromSource(string spec, PackageStore store) => throw Unsupported();

    /// <inheritdoc />
    public bool Remove(string id) => throw Unsupported();

    /// <inheritdoc />
    public void Embed(string id) => throw Unsupported();

    /// <inheritdoc />
    public void Revert(string id) => throw Unsupported();

    /// <inheritdoc />
    public void CopyAssets(string id, IReadOnlyCollection<string> assets, string destination) => throw Unsupported();

    /// <inheritdoc />
    public void AddRegistry(ScopedRegistry registry) => throw Unsupported();

    /// <inheritdoc />
    public bool RemoveRegistry(string name) => throw Unsupported();

    /// <inheritdoc />
    public void SaveManifest(ProjectManifest manifest) => throw Unsupported();

    static PackageException Unsupported() => new("Not supported by the in-memory workspace.");
}
