namespace Gaya.Packages.Editor;

/// <summary>What a staged brick change does.</summary>
public enum BrickChangeKind
{
    /// <summary>Start using the brick.</summary>
    Enable,

    /// <summary>Stop using the brick, keeping it on this machine.</summary>
    Disable,

    /// <summary>Move the brick to its newest version.</summary>
    Update,

    /// <summary>Use exactly one version of the brick, older or newer.</summary>
    UseVersion,
}

/// <summary>A change the Bricks panel holds until it is applied together with the others, in one pass.</summary>
/// <param name="Kind">What the change does.</param>
/// <param name="Id">The brick id.</param>
/// <param name="Scope">Whose workspace it changes.</param>
/// <param name="Source">
/// What to declare: the source to enable from, the exact version to use, or for an update the range that admits the
/// newest version when the declared one does not; null to update within the declared range.
/// </param>
public sealed record BrickChange(BrickChangeKind Kind, string Id, BrickScope Scope, string? Source = null)
{
    /// <summary>The version in use before the change; null when the brick is not in use.</summary>
    public string? From { get; init; }

    /// <summary>The version the change leads to; null when it stops using the brick, or when it is not known.</summary>
    public string? To { get; init; }

    /// <summary>The brick whose change this one follows from, such as disabling a brick it needs; null when chosen directly.</summary>
    public string? Because { get; init; }

    /// <summary>The change as a version transition: <c>1.0.1 → 1.0.2</c>, <c>disabled → 1.0.1</c> or <c>2.1.1 → disabled</c>.</summary>
    public string Transition => $"{From ?? "disabled"} → {(Kind == BrickChangeKind.Disable ? "disabled" : To ?? "newest")}";

    /// <summary>The change as the panel lists it.</summary>
    public string Description =>
        $"{Id}: {Transition}{(Scope == BrickScope.Studio ? " in Studio" : "")}{(Because is null ? "" : $" (needs {Because})")}";
}

public sealed partial class BricksController
{
    readonly Lock stagedLock = new();
    List<BrickChange> staged = [];

    /// <summary>The changes waiting for <see cref="ApplyPendingAsync"/>, in the order they were made.</summary>
    public IReadOnlyList<BrickChange> Pending
    {
        get
        {
            lock (stagedLock)
            {
                return [.. staged];
            }
        }
    }

    /// <summary>The change waiting for a brick, or null.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>The pending change.</returns>
    public BrickChange? PendingFor(string id)
    {
        lock (stagedLock)
        {
            return staged.LastOrDefault(c => c.Id == id);
        }
    }

    /// <summary>Holds changes until <see cref="ApplyPendingAsync"/>, replacing any change already waiting for those bricks.</summary>
    /// <param name="changes">The changes.</param>
    public void Stage(params BrickChange[] changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (stagedLock)
        {
            staged = [.. staged.Where(c => changes.All(change => change.Id != c.Id)), .. changes];
        }

        Notify();
    }

    /// <summary>Drops the change waiting for a brick, with the changes that followed from it.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether a change was waiting.</returns>
    public bool Unstage(string id)
    {
        bool removed;
        lock (stagedLock)
        {
            var root = staged.LastOrDefault(c => c.Id == id) is { Because: { } cause } ? cause : id;
            removed = staged.RemoveAll(c => c.Id == root || c.Because == root) > 0;
        }

        Notify();
        return removed;
    }

    /// <summary>Drops every waiting change.</summary>
    public void DiscardPending()
    {
        lock (stagedLock)
        {
            staged = [];
        }

        Notify();
    }

    /// <summary>
    /// Flips a brick, as its checkbox does: drops the change waiting for it, else stages enabling a brick that is not
    /// in use, in the scope it belongs to, or disabling one that is, together with every brick in use that needs it.
    /// </summary>
    /// <param name="brick">The brick.</param>
    public void Toggle(CatalogBrick brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        if (Unstage(brick.Id))
        {
            return;
        }

        if (brick.IsInstalled)
        {
            var scope = ScopeFor(brick);
            Stage([new BrickChange(BrickChangeKind.Disable, brick.Id, scope) { From = brick.InstalledVersion },
                .. Dependents(brick.Id).Select(p => new BrickChange(BrickChangeKind.Disable, p.Id, scope)
                {
                    From = p.Version.ToString(),
                    Because = brick.Id,
                })]);
        }
        else if (EnableProblem(brick) is { } problem)
        {
            Publish(installed, problem);
            Report(problem);
        }
        else
        {
            Stage(new BrickChange(BrickChangeKind.Enable, brick.Id, ScopeFor(brick), brick.InstallSource) { To = brick.LatestVersion });
        }
    }

    /// <summary>
    /// Stages moving a brick to its newest version. A declared range that excludes it is replaced: an exact version
    /// by the newest one, any other range by one starting there. A brick linked to a local folder is refused, since
    /// the folder decides its version.
    /// </summary>
    /// <param name="brick">The brick in use.</param>
    public void StageUpdate(CatalogBrick brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        if (brick.Link is { } link)
        {
            Report($"{brick.Id} is linked to {link} on this machine, which decides its version; remove the link to update it from its registry.");
            return;
        }

        var scope = ScopeFor(brick);
        var declared = WorkspaceOf(scope) is { } workspace ? Declared(workspace.Root, brick.Id) : null;
        var source = brick.LatestVersion is { } latest && SemanticVersion.TryParse(latest, out var newest)
                     && VersionRange.TryParse(declared, out var range) && !range.IsSatisfiedBy(newest)
            ? SemanticVersion.TryParse(declared, out _) ? latest : $"^{latest}"
            : null;
        Stage(new BrickChange(BrickChangeKind.Update, brick.Id, scope, source) { From = brick.InstalledVersion, To = brick.LatestVersion });
    }

    /// <summary>Stages using exactly one version of a brick, which also downgrades.</summary>
    /// <param name="brick">The brick.</param>
    /// <param name="version">The version a registry lists.</param>
    public void StageVersion(CatalogBrick brick, string version)
    {
        ArgumentNullException.ThrowIfNull(brick);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Stage(new BrickChange(BrickChangeKind.UseVersion, brick.Id, ScopeFor(brick), version.Trim())
        {
            From = brick.IsInstalled ? brick.InstalledVersion : null,
            To = version.Trim(),
        });
    }

    /// <summary>
    /// Applies every waiting change in one pass per workspace, so the project resolves and recompiles once however
    /// many bricks changed. A change that fails, or an update that leaves the version as it was, is reported with the
    /// reason, without stopping the others. When every change was for the other scope, the panel moves there.
    /// </summary>
    /// <returns>Whether every change applied.</returns>
    public async Task<bool> ApplyPendingAsync()
    {
        List<BrickChange> batch;
        lock (stagedLock)
        {
            (batch, staged) = (staged, []);
        }

        if (batch.Count == 0)
        {
            return true;
        }

        var failures = new List<string>();
        var succeeded = true;
        foreach (var group in batch.GroupBy(static c => c.Scope))
        {
            var changes = group.ToList();
            var label = changes.Count == 1 ? changes[0].Description : $"Apply {changes.Count} brick changes";
            succeeded &= await RunAsync(label, workspace => ApplyBatch(workspace, changes, failures),
                target: WorkspaceOf(group.Key)).ConfigureAwait(false);
        }

        if (batch.All(c => c.Scope != Scope) && WorkspaceOf(batch[0].Scope) is not null)
        {
            Scope = batch[0].Scope;
        }

        if (failures.Count > 0)
        {
            var message = string.Join('\n', failures.Distinct(StringComparer.Ordinal));
            Publish(installed, message);
            if (succeeded)
            {
                Report(message);
            }
        }

        return succeeded && failures.Count == 0;
    }

    /// <summary>Stops linking a brick to a local folder, so the workspace uses it from its declared source again.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the brick was linked.</returns>
    public Task<bool> UnlinkAsync(string id) => RunAsync($"Remove the link of {id}", workspace =>
    {
        if (!workspace.Unlink(id))
        {
            throw new PackageException($"{id} is not linked to a local folder.");
        }
    });

    // Every brick in use that needs id, directly or through another brick in use.
    IReadOnlyList<ResolvedPackage> Dependents(string id)
    {
        var found = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        var pending = new Stack<string>([id]);
        while (pending.TryPop(out var needed))
        {
            foreach (var dependent in installed.Where(p => p.Manifest.Dependencies.ContainsKey(needed) && p.Id != id))
            {
                if (found.TryAdd(dependent.Id, dependent))
                {
                    pending.Push(dependent.Id);
                }
            }
        }

        return [.. found.Values];
    }

    // Applies each change, then the plain updates in one resolution; throws only when nothing applied.
    static void ApplyBatch(IBrickWorkspace workspace, IReadOnlyList<BrickChange> changes, List<string> failures)
    {
        var applied = 0;
        void Attempt(Action change)
        {
            try
            {
                change();
                applied++;
            }
            catch (PackageException ex)
            {
                failures.Add(ex.Message);
            }
        }

        var updating = changes.Any(static c => c.Kind is BrickChangeKind.Update or BrickChangeKind.UseVersion);
        var before = updating ? Versions(workspace) : null;
        foreach (var change in changes.Where(static c => c is not { Kind: BrickChangeKind.Update, Source: null }))
        {
            // A brick that the bricks this batch disables bring in leaves with them, even when it has no declaration
            // of its own to drop and refuses to be disabled alone.
            if (change.Kind == BrickChangeKind.Disable && changes.Any(c => c.Because == change.Id))
            {
                try
                {
                    workspace.Disable(change.Id);
                }
                catch (PackageException)
                {
                }

                applied++;
                continue;
            }

            Attempt(() =>
            {
                if (change.Kind == BrickChangeKind.Disable)
                {
                    workspace.Disable(change.Id);
                }
                else
                {
                    workspace.Add(change.Id, change.Source);
                }
            });
        }

        var updates = changes.Where(static c => c is { Kind: BrickChangeKind.Update, Source: null }).Select(static c => c.Id).ToList();
        if (updates.Count > 0)
        {
            Attempt(() => workspace.Update(updates));
        }

        if (before is not null)
        {
            failures.AddRange(Unchanged(workspace, changes, before));
        }

        if (applied == 0)
        {
            throw new PackageException(string.Join('\n', failures));
        }
    }

    // Says why each update or version choice that ran left its brick where it was.
    static IEnumerable<string> Unchanged(IBrickWorkspace workspace, IReadOnlyList<BrickChange> changes,
        IReadOnlyDictionary<string, ResolvedPackage> before)
    {
        var after = Versions(workspace);
        foreach (var change in changes.Where(static c => c.Kind is BrickChangeKind.Update or BrickChangeKind.UseVersion))
        {
            if (!before.TryGetValue(change.Id, out var old) || !after.TryGetValue(change.Id, out var now) || now.Version != old.Version)
            {
                continue;
            }

            yield return now.Origin == PackageOrigin.Link
                ? $"{change.Id} stays at {now.Version}: it is linked to {now.Source.Replace("link:", "", StringComparison.Ordinal)} on this machine, which decides its version. Remove the link to use the registry's."
                : $"{change.Id} stays at {now.Version}: no newer version fits what the project and its other bricks require.";
        }
    }

    static IReadOnlyDictionary<string, ResolvedPackage> Versions(IBrickWorkspace workspace)
    {
        try
        {
            return workspace.Resolve().ToDictionary(static p => p.Id, StringComparer.Ordinal);
        }
        catch (PackageException)
        {
            return new Dictionary<string, ResolvedPackage>();
        }
    }

    // What the workspace's own manifest declares for a brick; null when it only arrives as a dependency.
    static string? Declared(string root, string id)
    {
        try
        {
            return ProjectManifest.Load(root, includeUserOverride: false).Manifest.Dependencies.GetValueOrDefault(id);
        }
        catch (PackageException)
        {
            return null;
        }
    }

    void Notify()
    {
        Revision++;
        Changed?.Invoke();
    }
}
