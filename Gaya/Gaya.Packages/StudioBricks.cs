namespace Gaya.Packages;

/// <summary>
/// The studio's own bricks: the per-user manifest in <c>~/.gaya/studio/Bricks</c>, resolved with
/// <see cref="PackageScope.Studio"/> against the running hosts. These bricks install plugins, themes, icon themes and
/// font packs into the application rather than into a project, so they cannot be forked.
/// </summary>
/// <param name="root">The studio folder holding the <c>Bricks</c> manifest.</param>
/// <param name="hosts">Host → version, checked against each brick's <c>engines</c>.</param>
/// <param name="store">The shared store; the default one when null.</param>
/// <param name="publicRegistry">The registry every studio takes bricks from, or null.</param>
public sealed class StudioBricks(string root, IReadOnlyDictionary<string, SemanticVersion> hosts,
    PackageStore? store = null, ScopedRegistry? publicRegistry = null) : IBrickWorkspace
{
    /// <summary>The folder, inside a brick, holding its compiled plugin assemblies.</summary>
    public const string LibraryDirectoryName = "Precast~/lib";

    readonly PackageStore packages = store ?? new PackageStore(PackageStore.DefaultRoot());

    /// <inheritdoc />
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Host → version, checked against each brick's <c>engines</c>.</summary>
    public IReadOnlyDictionary<string, SemanticVersion> Hosts { get; } = hosts;

    /// <inheritdoc />
    public PackageScope Scope => PackageScope.Studio;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReservedCategoryPrefixes { get; } = ["gaya", .. hosts.Keys.Where(host => host != "gaya")];

    /// <inheritdoc />
    public string? BuiltinDirectory => null;

    /// <inheritdoc />
    public ScopedRegistry? PublicRegistry { get; } = publicRegistry;

    /// <inheritdoc />
    public bool SupportsForks => false;

    /// <summary>Raised after an operation changed the studio's bricks, on the thread that performed it.</summary>
    public event Action<bool>? Applied;

    /// <summary>Whether the studio declares any bricks.</summary>
    public bool HasManifest => File.Exists(Path.Combine(Root, ProjectManifest.DirectoryName, ProjectManifest.FileName));

    /// <inheritdoc />
    public IReadOnlyList<ResolvedPackage> Resolve() => ResolveAll().Packages;

    /// <summary>Resolves the studio's bricks and writes the lock file unless a user override applied.</summary>
    /// <param name="locked">Install exactly what the lock file records.</param>
    /// <returns>The resolution; empty when the studio declares no bricks.</returns>
    /// <exception cref="PackageException">A brick cannot be fetched or does not fit.</exception>
    public PackageResolution ResolveAll(bool locked = false)
    {
        ProjectManifest.MigrateLegacyLayout(Root);
        if (!HasManifest) return new PackageResolution([], new LockFile());

        var resolution = Resolver(locked, new HashSet<string>()).ResolveAsync(Root).GetAwaiter().GetResult();
        if (!resolution.UsesUserOverride && !locked) resolution.Lock.Save(Root);
        return resolution;
    }

    /// <inheritdoc />
    public void Add(string id, string? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Rollback(() => ProjectBricks.Add(Root, id, source ?? $"builtin:{id}"), Resolve);
    }

    /// <inheritdoc />
    public string AddFromSource(string spec, PackageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var id = PackageSource.Parse(spec, Path.Combine(Root, ProjectManifest.DirectoryName)) switch
        {
            FileSource file => PackageManifest.Load(file.Path, ReservedCategoryPrefixes).Name,
            ArchiveSource archive => BrickArchive.ReadManifest(archive.Path, ReservedCategoryPrefixes).Name,
            GitSource git => PackageManifest.Load(
                store.CheckoutAsync(git, store.ResolveCommitAsync(git).GetAwaiter().GetResult()).GetAwaiter().GetResult(),
                ReservedCategoryPrefixes).Name,
            _ => throw new PackageException("Bricks from a registry are added from the catalog."),
        };
        Add(id, spec);
        return id;
    }

    /// <inheritdoc />
    public void Disable(string id)
    {
        var (manifest, _) = ProjectManifest.Load(Root, includeUserOverride: false);
        if (!manifest.Dependencies.ContainsKey(id))
            throw new PackageException($"The studio does not declare {id}.");
        Rollback(() =>
        {
            _ = manifest.Dependencies.Remove(id);
            manifest.Save(Root);
        }, Resolve);
    }

    /// <inheritdoc />
    public bool Remove(string id) => ProjectBricks.Remove(Root, id);

    /// <inheritdoc />
    public void Update(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!HasManifest) return;
        var resolution = Resolver(locked: false, ids.Count > 0 ? ids.ToHashSet(StringComparer.Ordinal) : null)
            .ResolveAsync(Root).GetAwaiter().GetResult();
        if (!resolution.UsesUserOverride) resolution.Lock.Save(Root);
    }

    /// <inheritdoc />
    public void Restore() => _ = ResolveAll();

    /// <inheritdoc />
    public void Embed(string id) => throw new PackageException("Studio bricks cannot be made local.");

    /// <inheritdoc />
    public void Revert(string id) => throw new PackageException("Studio bricks cannot be made local.");

    /// <inheritdoc />
    public void CopyAssets(string id, IReadOnlyCollection<string> assets, string destination) =>
        throw new PackageException("Studio bricks have no project to copy assets into.");

    /// <inheritdoc />
    public IReadOnlyList<ScopedRegistry> Registries()
    {
        var declared = HasManifest ? ProjectManifest.Load(Root).Manifest.ScopedRegistries : [];
        return PublicRegistry is { } shared ? [.. declared, shared] : [.. declared];
    }

    /// <inheritdoc />
    public void AddRegistry(ScopedRegistry registry)
    {
        BrickRegistries.Validate(registry);
        var (manifest, _) = ProjectManifest.Load(Root, includeUserOverride: false);
        manifest.ScopedRegistries.RemoveAll(existing => existing.Name == registry.Name);
        manifest.ScopedRegistries.Add(registry);
        manifest.Save(Root);
    }

    /// <inheritdoc />
    public bool RemoveRegistry(string name)
    {
        var (manifest, _) = ProjectManifest.Load(Root, includeUserOverride: false);
        if (manifest.ScopedRegistries.RemoveAll(existing => existing.Name == name) == 0) return false;
        manifest.Save(Root);
        return true;
    }

    /// <inheritdoc />
    public void SaveManifest(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var registry in manifest.ScopedRegistries) BrickRegistries.Validate(registry);
        Rollback(() => manifest.Save(Root), Resolve);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string query) =>
        BrickRegistries.SearchAsync(Registries(), query);

    /// <inheritdoc />
    public bool IsPrecast(ResolvedPackage brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        return Directory.Exists(Path.Combine(brick.RootPath, LibraryDirectoryName));
    }

    /// <inheritdoc />
    public void Changed(bool applied) => Applied?.Invoke(applied);

    PackageResolver Resolver(bool locked, IReadOnlySet<string>? update) => new(packages, new PackageResolverOptions
    {
        Hosts = Hosts,
        Scope = PackageScope.Studio,
        Registries = PublicRegistry is { } shared ? [shared] : [],
        ReservedCategoryPrefixes = ReservedCategoryPrefixes,
        Locked = locked,
        Update = update,
    });

    /// <summary>Runs an edit, then a check; when the check fails the manifest file is put back as it was.</summary>
    void Rollback(Action edit, Func<object> check)
    {
        var path = Path.Combine(Root, ProjectManifest.DirectoryName, ProjectManifest.FileName);
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        edit();
        try
        {
            _ = check();
        }
        catch (PackageException)
        {
            if (before is null) File.Delete(path);
            else File.WriteAllText(path, before);
            throw;
        }
    }
}

/// <summary>Registry checks and searches shared by every brick workspace.</summary>
public static class BrickRegistries
{
    /// <summary>Checks that a registry has a name, an address, scopes, and keys or explicit trust in unsigned bricks.</summary>
    /// <param name="registry">The registry.</param>
    /// <exception cref="PackageException">The registry is incomplete or a key is not an OpenSSH ed25519 public key.</exception>
    public static void Validate(ScopedRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (new[] { registry.Name, registry.Url }.Any(string.IsNullOrWhiteSpace) || registry.Scopes.Count == 0
            || registry.Scopes.Any(string.IsNullOrWhiteSpace))
            throw new PackageException("A registry needs a name, an address and at least one scope.");
        foreach (var key in registry.Keys) _ = SshSignature.ParsePublicKey(key);
        if (registry.Keys.Count == 0 && !registry.AllowUnsigned)
            throw new PackageException(
                "A registry needs at least one trusted key (an OpenSSH public key), or must be marked to allow unsigned bricks.");
    }

    /// <summary>Finds bricks in <paramref name="registries"/> whose id contains <paramref name="query"/>.</summary>
    /// <param name="registries">The registries to read.</param>
    /// <param name="query">Text the id must contain; every brick when empty.</param>
    /// <returns>Registry, id and newest version; an unreadable registry is reported in place of its bricks.</returns>
    public static async Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(
        IEnumerable<ScopedRegistry> registries, string query)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var found = new List<(string, string, string)>();
        foreach (var registry in registries)
        {
            try
            {
                var index = await new RegistryClient(registry).GetIndexAsync().ConfigureAwait(false);
                found.AddRange(index.Bricks.Where(b => b.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(b => (registry.Name, b.Key, Newest(b.Value))));
            }
            catch (PackageException ex)
            {
                found.Add((registry.Name, $"(unavailable: {ex.Message})", string.Empty));
            }
        }

        return found;
    }

    static string Newest(RegistryBrick brick) =>
        brick.Versions.Where(v => !v.Value.Yanked && SemanticVersion.TryParse(v.Key, out _))
            .Select(static v => SemanticVersion.Parse(v.Key)).OrderByDescending(static v => v).FirstOrDefault()
            ?.ToString() ?? "(yanked)";
}
