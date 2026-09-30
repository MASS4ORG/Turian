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

    /// <summary>What the last <see cref="Refresh"/> found, dependencies first.</summary>
    public IReadOnlyList<BrickRow> Rows { get; private set; } = [];

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
        Rows =
        [
            .. bricks.Select(static p => new BrickRow(p.Id, p.Version.ToString(), p.Manifest.DisplayName, p.Origin, p.Source,
                p.Depth == 1, p.IsOverridden, BrickAssemblies.IsPrecast(p))),
        ];
        Error = error;
        if (Selected is not null && bricks.All(p => p.Id != Selected)) Selected = null;
        Changed?.Invoke();
    }

    void SetBusy(bool busy)
    {
        IsBusy = busy;
        Changed?.Invoke();
    }
}
