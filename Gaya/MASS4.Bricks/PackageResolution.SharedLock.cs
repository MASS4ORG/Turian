namespace MASS4.Bricks;

public sealed partial record PackageResolution
{
    /// <summary>
    /// The lock to commit: what this resolution chose, except that each package the per-user override chose keeps its
    /// committed entry, along with the committed entries it depends on that this resolution no longer has. An update
    /// of a shared package is thereby recorded while a personal link never reaches the shared file.
    /// </summary>
    /// <param name="committed">The lock file on disk, or null when the project has none.</param>
    /// <returns>The lock to save.</returns>
    public LockFile SharedLock(LockFile? committed)
    {
        if (!UsesUserOverride)
        {
            return Lock;
        }

        var shared = new LockFile();
        foreach (var (id, entry) in Lock.Dependencies)
        {
            shared.Dependencies[id] = entry;
        }

        var pending = new Stack<string>(Packages.Where(static p => p.IsOverridden).Select(static p => p.Id));
        foreach (var id in pending)
        {
            _ = shared.Dependencies.Remove(id);
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var id))
        {
            if (!kept.Add(id) || committed?.Dependencies.GetValueOrDefault(id) is not { } entry
                || shared.Dependencies.ContainsKey(id))
            {
                continue;
            }

            shared.Dependencies[id] = entry;
            foreach (var dependency in entry.Dependencies.Keys)
            {
                pending.Push(dependency);
            }
        }

        return shared;
    }
}
