namespace Gaya.Packages.Editor;

/// <summary>Whose bricks the Bricks panel manages.</summary>
public enum BrickScope
{
    /// <summary>The open workspace's, such as a game project's.</summary>
    Project,

    /// <summary>The studio's own, installed for the user into the application.</summary>
    Studio,
}

/// <summary>What the Bricks panel lists.</summary>
public enum BricksTab
{
    /// <summary>The bricks the project could use.</summary>
    Bricks,

    /// <summary>The registries the project takes bricks from.</summary>
    Registries,
}

/// <summary>One installed brick as the Bricks panel lists it.</summary>
/// <param name="Id">The brick id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="DisplayName">The name shown to users, or null.</param>
/// <param name="Origin">How the brick reached the workspace.</param>
/// <param name="Source">The source as the manifest declares it.</param>
/// <param name="IsDirect">Whether the workspace installs it itself rather than another brick needing it.</param>
/// <param name="IsOverridden">Whether the per-user override chose its source.</param>
/// <param name="IsPrecast">Whether it ships prebuilt assemblies the host loads instead of compiling.</param>
public sealed record BrickRow(string Id, string Version, string? DisplayName, PackageOrigin Origin, string Source,
    bool IsDirect, bool IsOverridden, bool IsPrecast);

/// <summary>
/// The state and actions behind the Bricks panel: what the studio or the open workspace installs, what depends on
/// what, and the install, update, remove and embed actions, each run as a background task so the editor stays
/// responsive. The panel draws this and holds no logic of its own.
/// </summary>
/// <param name="studio">The studio's own bricks, or null when the host has none.</param>
/// <param name="project">Supplies the open workspace, or null when the host has no workspaces.</param>
/// <param name="runner">Runs the actions in the background; the thread pool when null.</param>
/// <param name="logger">Receives failures the panel only summarises.</param>
public sealed class BricksController(IBrickWorkspace? studio, IProjectBricks? project, IBrickTaskRunner? runner = null,
    ILogger? logger = null)
{
    readonly IBrickTaskRunner tasks = runner ?? new ThreadPoolBrickTaskRunner();
    readonly ILogger log = logger ?? NullLogger.Instance;
    IReadOnlyList<ResolvedPackage> installed = [];
    IReadOnlyList<(string Registry, string Id, string Latest)> registryBricks = [];
    string? registryRoot;

    /// <summary>What the last <see cref="Refresh"/> found, dependencies first.</summary>
    public IReadOnlyList<BrickRow> Rows { get; private set; } = [];

    /// <summary>
    /// Every brick the workspace could use: installed ones, enabled or not, and available ones from the built-in
    /// bricks, the shared store and, once <see cref="RefreshRegistriesAsync"/> has run, the registries.
    /// </summary>
    public IReadOnlyList<CatalogBrick> Catalog { get; private set; } = [];

    /// <summary>The shared store whose downloaded bricks the catalog lists.</summary>
    public PackageStore Store { get; set; } = new(PackageStore.DefaultRoot());

    /// <summary>The id of the brick selected in the panel.</summary>
    public string? Selected { get; set; }

    /// <summary>The name of the registry selected in the panel; empty for a new one.</summary>
    public string? SelectedRegistry { get; set; }

    /// <summary>What the panel lists.</summary>
    public BricksTab Tab { get; set; }

    /// <summary>Whose bricks are managed; changing it rereads the list.</summary>
    public BrickScope Scope
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Selected = null;
            SelectedRegistry = null;
            Refresh();
        }
    }

    /// <summary>Whether the host has studio bricks to manage.</summary>
    public bool HasStudio => studio is not null;

    /// <summary>The bricks the last <see cref="Refresh"/> resolved, dependencies first.</summary>
    public IReadOnlyList<ResolvedPackage> Installed => installed;

    /// <summary>The workspace of the current <see cref="Scope"/>, or null when there is none.</summary>
    public IBrickWorkspace? Workspace => Scope == BrickScope.Studio ? studio : project?.Workspace;

    /// <summary>The registries the workspace takes bricks from, the public one last.</summary>
    public IReadOnlyList<ScopedRegistry> Registries { get; private set; } = [];

    /// <summary>Changes whenever the lists were rebuilt, so a view of a selection knows to refresh.</summary>
    public int Revision { get; private set; }

    /// <summary>What went wrong with the last resolve or action; null when it went well.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether an action is running.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Whether a workspace is open.</summary>
    public bool HasProject => project?.Workspace is not null;

    /// <summary>Whether the current <see cref="Scope"/> has a workspace to manage.</summary>
    public bool HasWorkspace => Workspace is not null;

    /// <summary>Raised when <see cref="Rows"/>, <see cref="Error"/> or <see cref="IsBusy"/> changed, from any thread.</summary>
    public event Action? Changed;

    /// <summary>Validates, saves and resolves the workspace's edited brick manifest.</summary>
    /// <param name="manifest">The edited manifest; a copy is saved.</param>
    /// <returns>Whether the manifest was saved.</returns>
    public Task<bool> SaveSettingsAsync(ProjectManifest manifest)
    {
        var snapshot = JsonSerializer.Deserialize<ProjectManifest>(
            JsonSerializer.Serialize(manifest, PackageJson.Options), PackageJson.Options)!;
        return RunAsync("Save brick settings", workspace => workspace.SaveManifest(snapshot));
    }

    /// <summary>Writes a registry into the project, replacing the one of the same name, or the one it was renamed from.</summary>
    /// <param name="previousName">The name the registry had when it was opened, or null for a new one.</param>
    /// <param name="registry">The registry as edited.</param>
    /// <returns>Whether the registry was saved.</returns>
    public Task<bool> SaveRegistryAsync(string? previousName, ScopedRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry = new ScopedRegistry
        {
            Name = registry.Name.Trim(),
            Url = registry.Url.Trim(),
            Scopes = [.. registry.Scopes.Select(s => s.Trim()).Where(s => s.Length > 0)],
            Keys = [.. registry.Keys.Select(s => s.Trim()).Where(s => s.Length > 0)],
            AllowUnsigned = registry.AllowUnsigned,
        };
        var before = SelectedRegistry;
        return RunAsync($"Save registry {registry.Name}", workspace =>
        {
            if (registry.Name != previousName && workspace.Registries().Any(r => r.Name == registry.Name))
                throw new PackageException($"A registry named {registry.Name} already exists.");

            workspace.AddRegistry(registry);
            if (previousName is not null && previousName != registry.Name) _ = workspace.RemoveRegistry(previousName);
            if (SelectedRegistry == before) SelectedRegistry = registry.Name;
        }, applyToProject: false);
    }

    /// <summary>Stops the project taking bricks from a registry.</summary>
    /// <param name="name">The registry's name.</param>
    /// <returns>Whether the project declared it.</returns>
    public Task<bool> RemoveRegistryAsync(string name) =>
        RunAsync($"Remove registry {name}", workspace =>
        {
            if (!workspace.RemoveRegistry(name)) throw new PackageException($"The workspace does not declare registry {name}.");
            if (SelectedRegistry == name) SelectedRegistry = null;
        }, applyToProject: false);

    /// <summary>The installed brick selected in the panel, or null.</summary>
    public ResolvedPackage? SelectedBrick => installed.FirstOrDefault(p => p.Id == Selected);

    /// <summary>The active workspace's folder, or null; a view of a selection acts only while this is still its own.</summary>
    public string? WorkspaceRoot => Workspace?.Root;

    /// <summary>The bricks that declare a dependency on <paramref name="id"/>: why it is installed besides the manifest.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>The ids of the dependents.</returns>
    public IReadOnlyList<string> RequiredBy(string id) =>
        [.. installed.Where(p => p.Manifest.Dependencies.ContainsKey(id)).Select(static p => p.Id)];

    /// <summary>Reads the workspace's bricks again.</summary>
    public void Refresh()
    {
        if (Workspace is not { } workspace)
        {
            Publish([], null);
            return;
        }

        try
        {
            Publish(workspace.Resolve(), null);
        }
        catch (PackageException ex)
        {
            Publish([], ex.Message);
        }
    }

    /// <summary>Installs a brick: declares it in the manifest, resolves, and brings the open project in line.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="source">The source; <c>builtin:&lt;id&gt;</c> when empty.</param>
    /// <returns>Whether the brick was installed.</returns>
    public Task<bool> InstallAsync(string id, string? source) =>
        RunAsync($"Install brick {id}", workspace =>
            workspace.Add(id.Trim(), string.IsNullOrWhiteSpace(source) ? null : source.Trim()));

    /// <summary>Makes the project use a catalog brick, fetching it from the origin this client knows for it.</summary>
    /// <param name="brick">The brick to use.</param>
    /// <returns>Whether the brick is in use.</returns>
    public Task<bool> EnableAsync(CatalogBrick brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        if (brick.InstallSource is null && !brick.IsBuiltin)
        {
            Publish(installed, $"{brick.Id} was downloaded before its origin was recorded; refresh the registries or add it from its source.");
            return Task.FromResult(false);
        }

        return InstallAsync(brick.Id, brick.InstallSource);
    }

    /// <summary>Makes the project use a brick from a folder, a <c>.brick</c> file or a git repository.</summary>
    /// <param name="spec">The source: <c>file:&lt;path&gt;</c> or <c>git+&lt;url&gt;[#ref]</c>.</param>
    /// <returns>Whether the brick is in use.</returns>
    public Task<bool> AddFromSourceAsync(string spec) =>
        RunAsync($"Add brick from {spec}", workspace => workspace.AddFromSource(spec.Trim(), Store));

    /// <summary>Stops the project using a brick without deleting it from this machine.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the project stopped declaring it.</returns>
    public Task<bool> DisableAsync(string id) => RunAsync($"Disable brick {id}", workspace => workspace.Disable(id));

    /// <summary>
    /// Deletes a brick from this machine: a local brick moves to the project's trash, a downloaded one leaves the
    /// shared store, which every project on the machine reads.
    /// </summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was deleted.</returns>
    public Task<bool> UninstallAsync(string id) =>
        RunAsync($"Uninstall brick {id}", workspace =>
        {
            if (installed.FirstOrDefault(p => p.Id == id) is { Origin: PackageOrigin.Embedded })
            {
                if (!workspace.Remove(id)) throw new PackageException($"{id} is not installed.");
                return;
            }

            if (installed.Any(p => p.Id == id))
                throw new PackageException($"{id} is in use; disable it before uninstalling it.");
            if (Store.Remove(id) == 0) throw new PackageException($"{id} is not in the shared store.");
        }, applyToProject: installed.Any(p => p.Id == id && p.Origin == PackageOrigin.Embedded));

    /// <summary>Reads the registries' bricks into <see cref="Catalog"/>; an unreachable registry is reported in <see cref="Error"/>.</summary>
    /// <returns>A task that completes when the catalog is updated.</returns>
    public async Task RefreshRegistriesAsync()
    {
        if (Workspace is not { } workspace) return;

        var root = workspace.Root;
        var found = await workspace.SearchAsync(string.Empty).ConfigureAwait(false);
        if (WorkspaceRoot != root) return;

        registryBricks = [.. found.Where(static f => f.Latest.Length > 0)];
        registryRoot = root;
        SaveRegistryCache(root);
        var unavailable = found.FirstOrDefault(static f => f.Latest.Length == 0);
        if (unavailable.Id is not null) log.LogWarning("Registry {Registry}: {Message}", unavailable.Registry, unavailable.Id);
        Publish(installed, unavailable.Id);
    }

    /// <summary>Removes a brick, preserving embedded sources in the project's trash.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was removed.</returns>
    public Task<bool> RemoveAsync(string id) =>
        RunAsync($"Remove brick {id}", workspace =>
        {
            if (!workspace.Remove(id)) throw new PackageException($"The workspace does not declare or embed {id}.");
        });

    /// <summary>Moves git bricks to their newest commit.</summary>
    /// <param name="ids">The bricks to update; all of them when empty.</param>
    /// <returns>Whether the update succeeded.</returns>
    public Task<bool> UpdateAsync(params string[] ids) =>
        RunAsync(ids.Length == 0 ? "Update bricks" : $"Update brick {string.Join(", ", ids)}", workspace => workspace.Update(ids));

    /// <summary>Fetches every brick the project needs.</summary>
    /// <returns>Whether every brick resolved.</returns>
    public Task<bool> RestoreAsync() => RunAsync("Restore bricks", workspace => workspace.Restore());

    /// <summary>Copies an installed brick into the project as a writable fork.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was embedded.</returns>
    public Task<bool> EmbedAsync(string id) => RunAsync($"Embed brick {id}", workspace => workspace.Embed(id));

    /// <summary>Goes back from a brick's local fork to its global version, keeping the fork in the project's trash.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick is global again.</returns>
    public Task<bool> RevertAsync(string id) => RunAsync($"Revert brick {id} to global", workspace => workspace.Revert(id));

    /// <summary>
    /// Copies assets of an installed brick into the project's <c>Assets/&lt;last id segment&gt;</c> folder under new ids,
    /// detached from the brick.
    /// </summary>
    /// <param name="id">The brick id.</param>
    /// <param name="assets">The assets to copy, relative to the brick folder.</param>
    /// <returns>Whether the assets were copied.</returns>
    public Task<bool> CopyAssetsAsync(string id, IReadOnlyCollection<string> assets) =>
        RunAsync($"Copy assets of {id}", workspace => workspace.CopyAssets(id, assets, id.Split('.')[^1]),
            applyToProject: false);

    async Task<bool> RunAsync(string label, Action<IBrickWorkspace> work, bool applyToProject = true)
    {
        if (Workspace is not { } workspace)
        {
            Publish(installed, Scope == BrickScope.Studio ? "This host has no studio bricks." : "Open a project first.");
            return false;
        }

        SetBusy(true);
        string? failure = null;
        try
        {
            var succeeded = await tasks.RunAsync(label, () =>
            {
                try
                {
                    work(workspace);
                }
                catch (PackageException ex)
                {
                    failure = ex.Message;
                    log.LogError("{Label} failed: {Message}", label, ex.Message);
                    throw;
                }

                workspace.Changed(applyToProject);
            }).ConfigureAwait(false);

            if (!succeeded) log.LogWarning("{Label} did not complete", label);
            Refresh();
            if (!succeeded) Publish(installed, failure ?? $"{label} failed");
            return succeeded;
        }
        finally
        {
            SetBusy(false);
        }
    }

    void Publish(IReadOnlyList<ResolvedPackage> bricks, string? error)
    {
        var workspace = Workspace;
        installed = bricks;
        LoadRegistryCache(workspace?.Root);
        Catalog = workspace is null
            ? []
            : BrickCatalog.WithRegistries(BrickCatalog.Local(bricks, workspace.BuiltinDirectory, Store,
                workspace.ReservedCategoryPrefixes, workspace.Scope), registryBricks);
        Rows =
        [
            .. bricks.Select(p => new BrickRow(p.Id, p.Version.ToString(), p.Manifest.DisplayName, p.Origin, p.Source,
                p.Depth == 1, p.IsOverridden, workspace?.IsPrecast(p) ?? false)),
        ];
        Registries = workspace is null ? [] : ReadRegistries(workspace);
        Error = error;
        if (Selected is not null && Catalog.All(b => b.Id != Selected)) Selected = null;
        Revision++;
        Changed?.Invoke();
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
        Path.Combine(Store.Root, ".registry-cache", $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]}.json");

    // What the registries offered last time, so the catalog lists available bricks before (or without) the network.
    void LoadRegistryCache(string? root)
    {
        if (registryRoot == root) return;

        registryRoot = root;
        registryBricks = [];
        if (root is null) return;

        try
        {
            if (File.Exists(RegistryCachePath(root)))
                registryBricks = JsonSerializer.Deserialize<List<RegistryCacheEntry>>(File.ReadAllText(RegistryCachePath(root)))?
                    .Select(static e => (e.Registry, e.Id, e.Latest)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "The registry cache could not be read");
        }
    }

    void SaveRegistryCache(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryCachePath(root))!);
            File.WriteAllText(RegistryCachePath(root), JsonSerializer.Serialize(
                registryBricks.Select(static b => new RegistryCacheEntry(b.Registry, b.Id, b.Latest))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "The registry cache could not be written");
        }
    }

    sealed record RegistryCacheEntry(string Registry, string Id, string Latest);

    void SetBusy(bool busy)
    {
        IsBusy = busy;
        Changed?.Invoke();
    }
}
