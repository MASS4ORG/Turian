using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>One installed brick as the Bricks panel lists it.</summary>
/// <param name="Id">The brick id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="DisplayName">The name shown to users, or null.</param>
/// <param name="Origin">How the brick reached the project.</param>
/// <param name="Source">The source as the manifest declares it.</param>
/// <param name="IsDirect">Whether the project installs it itself rather than another brick needing it.</param>
/// <param name="IsOverridden">Whether the per-user override chose its source.</param>
/// <param name="IsPrecast">Whether it ships prebuilt assemblies the project loads instead of compiling.</param>
public sealed record BrickRow(string Id, string Version, string? DisplayName, PackageOrigin Origin, string Source,
    bool IsDirect, bool IsOverridden, bool IsPrecast);

/// <summary>
/// The state and actions behind the Studio's Bricks panel: what the project installs, what depends on what, and the
/// install, update, remove and embed actions, each run as a background task so the editor stays responsive. The
/// panel draws this and holds no logic of its own.
/// </summary>
/// <param name="settings">Tells which project is open.</param>
/// <param name="runner">Runs the actions in the background.</param>
/// <param name="applier">Applies a change to the open project.</param>
/// <param name="logger">Receives failures the panel only summarises.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class BricksController(SettingsService settings, BackgroundTaskRunner runner, IBrickApplier applier, ILogger logger)
{
    IReadOnlyList<ResolvedPackage> installed = [];
    IReadOnlyList<(string Registry, string Id, string Latest)> registryBricks = [];
    string? registryRoot;

    /// <summary>What the last <see cref="Refresh"/> found, dependencies first.</summary>
    public IReadOnlyList<BrickRow> Rows { get; private set; } = [];

    /// <summary>
    /// Every brick the project could use: installed ones, enabled or not, and available ones from the built-in bricks,
    /// the shared store and, once <see cref="RefreshRegistriesAsync"/> has run, the registries.
    /// </summary>
    public IReadOnlyList<CatalogBrick> Catalog { get; private set; } = [];

    /// <summary>The shared store whose downloaded bricks the catalog lists.</summary>
    public PackageStore Store { get; init; } = new(PackageStore.DefaultRoot());

    /// <summary>The id of the brick the panel shows details of.</summary>
    public string? Selected { get; set; }

    /// <summary>What went wrong with the last resolve or action; null when it went well.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether an action is running.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Whether a project is open.</summary>
    public bool HasProject => ProjectRoot is not null;

    /// <summary>Raised when <see cref="Rows"/>, <see cref="Error"/> or <see cref="IsBusy"/> changed, from any thread.</summary>
    public event Action? Changed;

    /// <summary>The installed brick selected in the panel, or null.</summary>
    public ResolvedPackage? SelectedBrick => installed.FirstOrDefault(p => p.Id == Selected);

    string? ProjectRoot => settings is { HasSettings: true, Settings.ProjectAbsoluteDir: { Length: > 0 } root } ? root : null;

    /// <summary>The bricks that declare a dependency on <paramref name="id"/>: why it is installed besides the manifest.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>The ids of the dependents.</returns>
    public IReadOnlyList<string> RequiredBy(string id) =>
        [.. installed.Where(p => p.Manifest.Dependencies.ContainsKey(id)).Select(static p => p.Id)];

    /// <summary>Reads the project's bricks again.</summary>
    public void Refresh()
    {
        if (ProjectRoot is not { } root)
        {
            Publish([], null);
            return;
        }

        try
        {
            Publish(ProjectPackages.Resolve(root).Packages, null);
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
        RunAsync($"Install brick {id}", root => BrickService.Add(root, id.Trim(), string.IsNullOrWhiteSpace(source) ? null : source.Trim()));

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
        RunAsync($"Add brick from {spec}", root => BrickService.AddFromSource(root, spec.Trim(), Store));

    /// <summary>Stops the project using a brick without deleting it from this machine.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the project stopped declaring it.</returns>
    public Task<bool> DisableAsync(string id) => RunAsync($"Disable brick {id}", root => BrickService.Disable(root, id));

    /// <summary>
    /// Deletes a brick from this machine: a local brick moves to the project's trash, a downloaded one leaves the
    /// shared store, which every project on the machine reads.
    /// </summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was deleted.</returns>
    public Task<bool> UninstallAsync(string id) =>
        RunAsync($"Uninstall brick {id}", root =>
        {
            if (installed.FirstOrDefault(p => p.Id == id) is { Origin: PackageOrigin.Embedded })
            {
                if (!BrickService.Remove(root, id)) throw new PackageException($"{id} is not installed.");
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
        if (ProjectRoot is not { } root) return;

        var found = await BrickService.SearchAsync(root, string.Empty).ConfigureAwait(false);
        if (ProjectRoot != root) return;

        registryBricks = [.. found.Where(static f => f.Latest.Length > 0)];
        registryRoot = root;
        SaveRegistryCache(root);
        var unavailable = found.FirstOrDefault(static f => f.Latest.Length == 0);
        Publish(installed, unavailable.Id);
    }

    /// <summary>Removes a brick, preserving embedded sources in the project's trash.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was removed.</returns>
    public Task<bool> RemoveAsync(string id) =>
        RunAsync($"Remove brick {id}", root =>
        {
            if (!BrickService.Remove(root, id)) throw new PackageException($"The project does not declare or embed {id}.");
        });

    /// <summary>Moves git bricks to their newest commit.</summary>
    /// <param name="ids">The bricks to update; all of them when empty.</param>
    /// <returns>Whether the update succeeded.</returns>
    public Task<bool> UpdateAsync(params string[] ids) =>
        RunAsync(ids.Length == 0 ? "Update bricks" : $"Update brick {string.Join(", ", ids)}", root => BrickService.Update(root, ids));

    /// <summary>Fetches every brick the project needs.</summary>
    /// <returns>Whether every brick resolved.</returns>
    public Task<bool> RestoreAsync() => RunAsync("Restore bricks", root => BrickService.Restore(root));

    /// <summary>Copies an installed brick into the project as a writable fork.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was embedded.</returns>
    public Task<bool> EmbedAsync(string id) => RunAsync($"Embed brick {id}", root => BrickService.Embed(root, id));

    /// <summary>Goes back from a brick's local fork to its global version, keeping the fork in the project's trash.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick is global again.</returns>
    public Task<bool> RevertAsync(string id) => RunAsync($"Revert brick {id} to global", root => BrickService.Revert(root, id));

    /// <summary>
    /// Copies assets of an installed brick into the project's <c>Assets/&lt;last id segment&gt;</c> folder under new ids,
    /// detached from the brick.
    /// </summary>
    /// <param name="id">The brick id.</param>
    /// <param name="assets">The assets to copy, relative to the brick folder.</param>
    /// <returns>Whether the assets were copied.</returns>
    public Task<bool> CopyAssetsAsync(string id, IReadOnlyCollection<string> assets) =>
        RunAsync($"Copy assets of {id}", root => BrickService.CopyAssets(root, id, assets, id.Split('.')[^1]), applyToProject: false);

    async Task<bool> RunAsync(string label, Action<string> work, bool applyToProject = true)
    {
        if (ProjectRoot is not { } root)
        {
            Publish(installed, "Open a project first.");
            return false;
        }

        SetBusy(true);
        try
        {
            var status = await runner.RunAsync(
                new BackgroundTaskSpec { Label = label, Kind = BackgroundTaskKind.Generic, Locks = EditorLocks.Project },
                (progress, _) =>
                {
                    progress.Report(0, label);
                    work(root);
                    if (applyToProject) applier.ApplyBrickChanges();
                    progress.Report(1, label);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            var succeeded = status == BackgroundTaskStatus.Completed;
            if (!succeeded) logger.LogWarning("{Label} did not complete ({Status})", label, status);
            Refresh();
            if (!succeeded) Publish(installed, $"{label} failed");
            return succeeded;
        }
        finally
        {
            SetBusy(false);
        }
    }

    void Publish(IReadOnlyList<ResolvedPackage> bricks, string? error)
    {
        installed = bricks;
        LoadRegistryCache(ProjectRoot);
        Catalog = BrickCatalog.WithRegistries(BrickCatalog.Local(bricks, ProjectPackages.BuiltinDirectory, Store), registryBricks);
        Rows =
        [
            .. bricks.Select(static p => new BrickRow(p.Id, p.Version.ToString(), p.Manifest.DisplayName, p.Origin, p.Source,
                p.Depth == 1, p.IsOverridden, BrickAssemblies.IsPrecast(p))),
        ];
        Error = error;
        if (Selected is not null && bricks.All(p => p.Id != Selected)) Selected = null;
        Changed?.Invoke();
    }

    // A project's registries are its own, so each project has its own cache.
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
            logger.LogDebug(ex, "The registry cache could not be read");
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
            logger.LogDebug(ex, "The registry cache could not be written");
        }
    }

    sealed record RegistryCacheEntry(string Registry, string Id, string Latest);

    void SetBusy(bool busy)
    {
        IsBusy = busy;
        Changed?.Invoke();
    }
}
