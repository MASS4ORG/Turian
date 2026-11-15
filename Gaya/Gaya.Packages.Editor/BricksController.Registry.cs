namespace Gaya.Packages.Editor;

public sealed partial class BricksController
{
    // The registries are read over the network; a workspace whose registries cannot even be named lists none.
    static async Task<RegistryCatalog> ListRegistriesAsync(IBrickWorkspace workspace)
    {
        try
        {
            return await workspace.ListAsync().ConfigureAwait(false);
        }
        catch (PackageException ex)
        {
            return new RegistryCatalog([], [ex.Message]);
        }
    }

    IReadOnlyList<CatalogBrick> BuildCatalog(IBrickWorkspace workspace, IReadOnlyList<ResolvedPackage> bricks)
    {
        var local = BrickCatalog.Local(bricks, workspace.BuiltinDirectory, Store, workspace.ReservedCategoryPrefixes);
        return UsedElsewhere(WithUnresolved(BrickCatalog.WithRegistries(local, registryListings,
            workspace.ReservedCategoryPrefixes), workspace.Root, bricks), workspace);
    }

    /// <summary>
    /// Leaves out the bricks only the other scope accepts that its workspace already uses: they are managed there, and
    /// offering them here would only declare them again. "In use" therefore always means used by this workspace.
    /// </summary>
    IReadOnlyList<CatalogBrick> UsedElsewhere(IReadOnlyList<CatalogBrick> catalog, IBrickWorkspace workspace)
    {
        var other = WorkspaceOf(Scope == BrickScope.Studio ? BrickScope.Project : BrickScope.Studio);
        LockFile? locked;
        try
        {
            locked = other is null || ReferenceEquals(other, workspace) ? null : LockFile.Load(other.Root);
        }
        catch (PackageException)
        {
            locked = null;
        }

        return locked is null
            ? catalog
            :
            [.. catalog.Where(b => b.Accepts(workspace.Scope) || !locked.Dependencies.ContainsKey(b.Id))];
    }

    /// <summary>
    /// Lists the bricks the manifest declares but the resolution did not return, as in use, so a brick whose source
    /// cannot be reached can still be seen and disabled.
    /// </summary>
    static IReadOnlyList<CatalogBrick> WithUnresolved(IReadOnlyList<CatalogBrick> catalog, string root,
        IReadOnlyList<ResolvedPackage> resolved)
    {
        Dictionary<string, string?> declared;
        try
        {
            declared = ProjectManifest.Load(root, includeUserOverride: false).Manifest.Dependencies;
        }
        catch (PackageException)
        {
            return catalog;
        }

        var missing = declared.Where(pair => pair.Value is not null && resolved.All(p => p.Id != pair.Key)).ToList();
        if (missing.Count == 0)
        {
            return catalog;
        }

        var merged = catalog.ToDictionary(static b => b.Id, StringComparer.Ordinal);
        foreach (var (id, source) in missing)
        {
            merged[id] = merged.TryGetValue(id, out var known)
                ? known with { State = BrickState.Enabled, Origin = source }
                : new CatalogBrick(id, null, "Declared by the project, but its source could not be resolved.", null,
                    BrickState.Enabled, null, null, source, source, IsBuiltin: false);
        }

        return [.. merged.Values.OrderBy(static b => b.DisplayName ?? b.Id, StringComparer.OrdinalIgnoreCase)];
    }

    static IReadOnlyList<ScopedRegistry> ReadRegistries(IBrickWorkspace workspace)
    {
        try
        {
            return workspace.Registries();
        }
        catch (PackageException)
        {
            return workspace.PublicRegistry is { } shared ? [shared] : [];
        }
    }

    // A workspace's registries are its own, so each workspace has its own cache.
    string RegistryCachePath(string root) =>
        Path.Combine(Store.Root, ".registry-cache", $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]}.listings.json");

    // What the registries offered last time, so the catalog lists available bricks before (or without) the network.
    void LoadRegistryCache(string? root)
    {
        if (registryRoot == root)
        {
            return;
        }

        registryRoot = root;
        registryListings = [];
        if (root is null)
        {
            return;
        }

        try
        {
            if (File.Exists(RegistryCachePath(root)))
            {
                registryListings = [.. (JsonSerializer.Deserialize<List<RegistryListing>>(File.ReadAllText(RegistryCachePath(root)),
                    PackageJson.Options) ?? []).Where(static l => l is { Id: not null, Versions: not null })];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            log.LogDebug(ex, "The registry cache could not be read");
        }
    }

    void SaveRegistryCache(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryCachePath(root))!);
            File.WriteAllText(RegistryCachePath(root), JsonSerializer.Serialize(registryListings, PackageJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "The registry cache could not be written");
        }
    }
}
