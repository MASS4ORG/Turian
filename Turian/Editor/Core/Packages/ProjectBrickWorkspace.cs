using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// A Turian project's bricks as a host-neutral workspace for Gaya's Bricks panel. Every operation goes through
/// <see cref="BrickService"/>, so the panel, the command line and the studio share one behavior.
/// </summary>
/// <param name="root">The project folder.</param>
/// <param name="settings">Reloads the open project's brick settings after a change.</param>
/// <param name="applier">Brings the open project in line after its bricks changed.</param>
public sealed class ProjectBrickWorkspace(string root, SettingsService? settings = null, IBrickApplier? applier = null)
    : IBrickWorkspace
{
    /// <inheritdoc />
    public string Root { get; } = root;

    /// <inheritdoc />
    public PackageScope Scope => PackageScope.Project;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReservedCategoryPrefixes { get; } = ["gaya", ProjectPackages.HostName];

    /// <inheritdoc />
    public string? BuiltinDirectory => ProjectPackages.BuiltinDirectory;

    /// <inheritdoc />
    public ScopedRegistry? PublicRegistry => ProjectPackages.PublicRegistry;

    /// <inheritdoc />
    public bool SupportsForks => true;

    /// <inheritdoc />
    public IReadOnlyList<ResolvedPackage> Resolve() => ProjectPackages.Resolve(Root).Packages;

    /// <inheritdoc />
    public void Add(string id, string? source) => _ = BrickService.Add(Root, id, source);

    /// <inheritdoc />
    public string AddFromSource(string spec, PackageStore store) => BrickService.AddFromSource(Root, spec, store);

    /// <inheritdoc />
    public void Disable(string id) => BrickService.Disable(Root, id);

    /// <inheritdoc />
    public bool Remove(string id) => BrickService.Remove(Root, id);

    /// <inheritdoc />
    public void Update(IReadOnlyCollection<string> ids) => _ = BrickService.Update(Root, ids);

    /// <inheritdoc />
    public void Restore() => _ = BrickService.Restore(Root);

    /// <inheritdoc />
    public void Embed(string id) => _ = BrickService.Embed(Root, id);

    /// <inheritdoc />
    public void Revert(string id) => BrickService.Revert(Root, id);

    /// <inheritdoc />
    public void CopyAssets(string id, IReadOnlyCollection<string> assets, string destination) =>
        _ = BrickService.CopyAssets(Root, id, assets, destination);

    /// <inheritdoc />
    public IReadOnlyList<ScopedRegistry> Registries() => BrickService.Registries(Root);

    /// <inheritdoc />
    public void AddRegistry(ScopedRegistry registry) => BrickService.AddRegistry(Root, registry);

    /// <inheritdoc />
    public bool RemoveRegistry(string name) => BrickService.RemoveRegistry(Root, name);

    /// <inheritdoc />
    public void SaveManifest(ProjectManifest manifest) => _ = BrickService.SaveManifest(Root, manifest);

    /// <inheritdoc />
    public Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string query) =>
        BrickService.SearchAsync(Root, query);

    /// <inheritdoc />
    public bool IsPrecast(ResolvedPackage brick) => BrickAssemblies.IsPrecast(brick);

    /// <inheritdoc />
    public void Changed(bool applied)
    {
        if (applied) applier?.ApplyBrickChanges();
        if (settings?.Settings is { } project && project.ProjectAbsoluteDir == Root) project.Bricks.Reload();
    }
}

/// <summary>Supplies the open Turian project to the Bricks panel, and runs its operations as editor tasks.</summary>
/// <param name="settings">Tells which project is open.</param>
/// <param name="runner">Runs operations as background tasks holding the project lock.</param>
/// <param name="applier">Brings the open project in line after its bricks changed.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class TurianProjectBricks(SettingsService settings, BackgroundTaskRunner runner, IBrickApplier applier)
    : IProjectBricks, IBrickTaskRunner
{
    ProjectBrickWorkspace? workspace;

    /// <inheritdoc />
    public IBrickWorkspace? Workspace
    {
        get
        {
            if (settings is not { HasSettings: true, Settings.ProjectAbsoluteDir: { Length: > 0 } root }) return null;
            if (workspace?.Root != root) workspace = new ProjectBrickWorkspace(root, settings, applier);
            return workspace;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RunAsync(string label, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var status = await runner.RunAsync(
            new BackgroundTaskSpec { Label = label, Kind = BackgroundTaskKind.Generic, Locks = EditorLocks.Project },
            (progress, _) =>
            {
                progress.Report(0, label);
                work();
                progress.Report(1, label);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        return status == BackgroundTaskStatus.Completed;
    }
}
