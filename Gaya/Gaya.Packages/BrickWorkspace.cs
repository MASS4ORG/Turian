namespace Gaya.Packages;

/// <summary>
/// A folder whose bricks can be managed: a project, or the studio's per-user bricks. Every operation edits the
/// folder's <c>Bricks/manifest.json</c> and resolves again, so a declaration that does not resolve is rolled back.
/// </summary>
public interface IBrickWorkspace
{
    /// <summary>The folder holding the <c>Bricks</c> manifest.</summary>
    string Root { get; }

    /// <summary>Which bricks the workspace accepts.</summary>
    PackageScope Scope { get; }

    /// <summary>Category prefixes only hosts may use, such as <c>gaya</c>; passed when reading manifests.</summary>
    IReadOnlyCollection<string> ReservedCategoryPrefixes { get; }

    /// <summary>The folder of the host's built-in bricks, or <c>null</c> when it has none.</summary>
    string? BuiltinDirectory { get; }

    /// <summary>The registry every workspace takes bricks from and nobody edits, or <c>null</c>.</summary>
    ScopedRegistry? PublicRegistry { get; }

    /// <summary>Whether bricks can be copied into the workspace as forks, and their assets copied out.</summary>
    bool SupportsForks { get; }

    /// <summary>The resolved bricks, dependencies first; none when the workspace declares none.</summary>
    /// <exception cref="PackageException">A brick cannot be fetched or does not fit.</exception>
    IReadOnlyList<ResolvedPackage> Resolve();

    /// <summary>Declares a brick and resolves.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="source">The source; <c>builtin:&lt;id&gt;</c> when null.</param>
    void Add(string id, string? source);

    /// <summary>Declares a brick from a folder, a <c>.brick</c> file or a git repository, reading its id from it.</summary>
    /// <param name="spec">The source: <c>file:&lt;path&gt;</c> or <c>git+&lt;url&gt;[#ref]</c>.</param>
    /// <param name="store">The shared store a git brick is fetched into.</param>
    /// <returns>The brick id.</returns>
    string AddFromSource(string spec, PackageStore store);

    /// <summary>Stops declaring a brick, keeping it on this machine.</summary>
    /// <param name="id">The brick id.</param>
    void Disable(string id);

    /// <summary>Removes a declaration, keeping embedded sources in the workspace's trash.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the workspace declared or embedded it.</returns>
    bool Remove(string id);

    /// <summary>Fetches newer commits of git bricks.</summary>
    /// <param name="ids">The bricks to update; all of them when empty.</param>
    void Update(IReadOnlyCollection<string> ids);

    /// <summary>Fetches every brick the workspace needs.</summary>
    void Restore();

    /// <summary>Copies an installed brick into the workspace as a writable fork.</summary>
    /// <param name="id">The brick id.</param>
    void Embed(string id);

    /// <summary>Goes back from a fork to the brick's global version.</summary>
    /// <param name="id">The brick id.</param>
    void Revert(string id);

    /// <summary>Copies assets of an installed brick into the workspace, detached from it.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="assets">Asset paths relative to the brick folder.</param>
    /// <param name="destination">The destination folder, relative to the workspace's assets.</param>
    void CopyAssets(string id, IReadOnlyCollection<string> assets, string destination);

    /// <summary>The registries the workspace takes bricks from, <see cref="PublicRegistry"/> last.</summary>
    IReadOnlyList<ScopedRegistry> Registries();

    /// <summary>Declares a registry, replacing one of the same name.</summary>
    /// <param name="registry">The registry.</param>
    void AddRegistry(ScopedRegistry registry);

    /// <summary>Removes a declared registry.</summary>
    /// <param name="name">The registry's name.</param>
    /// <returns>Whether it was declared.</returns>
    bool RemoveRegistry(string name);

    /// <summary>Saves an edited manifest, restoring the previous one when it does not resolve.</summary>
    /// <param name="manifest">The manifest.</param>
    void SaveManifest(ProjectManifest manifest);

    /// <summary>Finds registry bricks whose id contains <paramref name="query"/>.</summary>
    /// <param name="query">Text the id must contain; every brick when empty.</param>
    /// <returns>Registry, id and newest version; an unreadable registry is reported with an empty version.</returns>
    Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string query);

    /// <summary>Whether a brick ships prebuilt assemblies the host loads.</summary>
    /// <param name="brick">The resolved brick.</param>
    bool IsPrecast(ResolvedPackage brick);

    /// <summary>
    /// Brings the running host in line after an operation, such as reloading plugins or rescanning themes. Runs on
    /// the thread that performed the operation.
    /// </summary>
    /// <param name="applied">Whether the operation changed which bricks are used, rather than only registries.</param>
    void Changed(bool applied);
}

/// <summary>Supplies the open workspace's bricks, such as a game project's, to the Bricks panel.</summary>
public interface IProjectBricks
{
    /// <summary>The open workspace, or <c>null</c> when none is open.</summary>
    IBrickWorkspace? Workspace { get; }
}

/// <summary>Runs brick operations off the UI thread, as the host's background tasks.</summary>
public interface IBrickTaskRunner
{
    /// <summary>Runs <paramref name="work"/> as a task named <paramref name="label"/>.</summary>
    /// <param name="label">What the task is shown as.</param>
    /// <param name="work">The operation; a thrown exception fails the task.</param>
    /// <returns>Whether the task completed.</returns>
    Task<bool> RunAsync(string label, Action work);
}

/// <summary>Runs brick operations on the thread pool when the host has no background task system.</summary>
public sealed class ThreadPoolBrickTaskRunner : IBrickTaskRunner
{
    /// <inheritdoc />
    public async Task<bool> RunAsync(string label, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await Task.Run(work).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is PackageException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
