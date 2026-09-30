namespace Gaya.Packages;

/// <summary>How a file of a fork differs from the upstream it was copied from.</summary>
public enum ForkChange
{
    /// <summary>The fork has a file upstream lacks.</summary>
    Added,

    /// <summary>The fork changed a file.</summary>
    Modified,

    /// <summary>The fork deleted a file.</summary>
    Removed,
}

/// <summary>One file of a fork that differs from its upstream.</summary>
/// <param name="Path">The file, relative to the brick folder, using <c>/</c>.</param>
/// <param name="Change">How it differs.</param>
public sealed record ForkDifference(string Path, ForkChange Change);

/// <summary>What <see cref="BrickFork.Rebase"/> did to each file.</summary>
/// <param name="Updated">Files taken from the new upstream because the fork had not touched them.</param>
/// <param name="Merged">Files both sides changed that merged without conflict.</param>
/// <param name="Conflicts">Files left holding conflict markers, or kept as the fork had them when they cannot be merged as text.</param>
/// <param name="Added">Files new upstream adds to the fork.</param>
/// <param name="Removed">Files new upstream deletes that the fork had not touched.</param>
public sealed record RebaseResult(
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Merged,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    /// <summary>Whether the fork needs hand editing before it is sound again.</summary>
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// Comparing and updating an embedded fork of a brick. A fork is a copy the project may edit; to take a new release of
/// the original, changes made on each side since the copy are merged file by file, three ways: what the fork was copied
/// from, what the original is now, and what the fork became.
/// </summary>
public static class BrickFork
{
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal) { ".git", "bin", "obj", "Precast~" };

    /// <summary>The files of <paramref name="forkRoot"/> that differ from <paramref name="upstreamRoot"/>.</summary>
    /// <param name="upstreamRoot">The original brick folder.</param>
    /// <param name="forkRoot">The fork's folder.</param>
    /// <returns>The differences, by path. The manifest's <c>upstream</c> note is not a difference.</returns>
    public static IReadOnlyList<ForkDifference> Diff(string upstreamRoot, string forkRoot)
    {
        var upstream = Snapshot(upstreamRoot);
        var fork = Snapshot(forkRoot);
        var differences = new List<ForkDifference>();

        foreach (var (path, hash) in fork)
        {
            if (!upstream.TryGetValue(path, out var original)) differences.Add(new ForkDifference(path, ForkChange.Added));
            else if (original != hash) differences.Add(new ForkDifference(path, ForkChange.Modified));
        }

        differences.AddRange(upstream.Keys.Where(path => !fork.ContainsKey(path)).Select(static path => new ForkDifference(path, ForkChange.Removed)));
        return [.. differences.OrderBy(static d => d.Path, StringComparer.Ordinal)];
    }

    /// <summary>Merges a new release of the original into a fork, in place.</summary>
    /// <param name="baseRoot">What the fork was copied from.</param>
    /// <param name="upstreamRoot">The new release of the original.</param>
    /// <param name="forkRoot">The fork's folder, which is changed.</param>
    /// <returns>What happened to each file.</returns>
    /// <exception cref="PackageException">Git, which merges the text files, is unavailable.</exception>
    public static RebaseResult Rebase(string baseRoot, string upstreamRoot, string forkRoot)
    {
        var original = Snapshot(baseRoot);
        var next = Snapshot(upstreamRoot);
        var fork = Snapshot(forkRoot);
        var (updated, merged, conflicts, added, removed) = (new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());

        foreach (var path in original.Keys.Concat(next.Keys).Concat(fork.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var inBase = original.TryGetValue(path, out var baseHash);
            var inNext = next.TryGetValue(path, out var nextHash);
            var inFork = fork.TryGetValue(path, out var forkHash);
            var forkChanged = inFork != inBase || (inFork && forkHash != baseHash);
            var nextChanged = inNext != inBase || (inNext && nextHash != baseHash);

            if (!nextChanged) continue;

            if (!forkChanged)
            {
                if (inNext)
                {
                    Copy(upstreamRoot, forkRoot, path);
                    (inFork ? updated : added).Add(path);
                }
                else
                {
                    File.Delete(Path.Combine(forkRoot, path));
                    removed.Add(path);
                }

                continue;
            }

            if (inFork && inNext && forkHash == nextHash) continue;

            if (!inFork || !inNext)
            {
                // One side deleted what the other changed: the fork's state stays, flagged for a decision.
                conflicts.Add(path);
                continue;
            }

            var result = inBase ? MergeText(baseRoot, upstreamRoot, forkRoot, path) : MergeText(null, upstreamRoot, forkRoot, path);
            (result == MergeOutcome.Clean ? merged : conflicts).Add(path);
        }

        return new RebaseResult(updated, merged, conflicts, added, removed);
    }

    enum MergeOutcome
    {
        Clean,
        Conflict,
    }

    static MergeOutcome MergeText(string? baseRoot, string upstreamRoot, string forkRoot, string path)
    {
        var forkFile = Path.Combine(forkRoot, path);
        var upstreamFile = Path.Combine(upstreamRoot, path);
        var baseFile = baseRoot is null ? null : Path.Combine(baseRoot, path);
        if (IsBinary(forkFile) || IsBinary(upstreamFile) || (baseFile is not null && IsBinary(baseFile))) return MergeOutcome.Conflict;

        // A file both sides added has no common ancestor: merge against an empty one, which conflicts wherever they differ.
        var emptyBase = baseFile ?? Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "merge-file", "-p", "-L", "fork", "-L", "original", "-L", "upstream", forkFile, emptyBase, upstreamFile })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new PackageException("git could not be started.");
            var merged = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode is < 0 or > 127) throw new PackageException($"git merge-file failed for {path}: {error.Trim()}");

            File.SetAttributes(forkFile, FileAttributes.Normal);
            File.WriteAllText(forkFile, merged);
            return process.ExitCode == 0 ? MergeOutcome.Clean : MergeOutcome.Conflict;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PackageException("git is not installed or not on the PATH; merging a fork needs it.", ex);
        }
        finally
        {
            if (baseFile is null && File.Exists(emptyBase)) File.Delete(emptyBase);
        }
    }

    static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(8000, stream.Length)];
        return stream.Read(buffer) > 0 && Array.IndexOf(buffer, (byte)0) >= 0;
    }

    static void Copy(string fromRoot, string toRoot, string path)
    {
        var target = Path.Combine(toRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
        File.Copy(Path.Combine(fromRoot, path), target, overwrite: true);
        File.SetAttributes(target, FileAttributes.Normal);
    }

    /// <summary>Every file of a brick with a hash of its content; the manifest is hashed without its <c>upstream</c> note.</summary>
    static Dictionary<string, string> Snapshot(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').SkipLast(1).Any(SkippedDirectories.Contains)) continue;

            files[relative] = relative == PackageManifest.FileName ? ManifestHash(file) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return files;
    }

    static string ManifestHash(string file)
    {
        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject manifest) return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        manifest.Remove("upstream");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }
}
