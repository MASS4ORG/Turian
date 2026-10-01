namespace Turian.Tests;

/// <summary>Comparing an embedded fork with its original and merging a new release into it.</summary>
public sealed class BrickForkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-fork-{Guid.NewGuid():N}");
    readonly string original;
    readonly string fork;
    readonly string release;

    /// <summary>Creates an original, a fork copied from it and a newer release, all identical to start with.</summary>
    public BrickForkTests()
    {
        original = Folder("original");
        fork = Folder("fork");
        release = Folder("release");
        foreach (var folder in new[] { original, fork, release })
        {
            Write(folder, "Runtime/Stats.txt", "one\ntwo\nthree\nfour\nfive\n");
            Write(folder, "Runtime/Config.json", "{\n  \"speed\": 1\n}\n");
            Write(folder, "Runtime/Unused.txt", "unused\n");
            File.WriteAllBytes(Path.Combine(folder, "Runtime", "Icon.bin"), [1, 0, 2, 0]);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A fork reports the files it added, changed and deleted, and ignores its own upstream note.</summary>
    [Fact]
    public void DiffListsWhatTheForkChanged()
    {
        Write(fork, "Runtime/Stats.txt", "ONE\ntwo\nthree\nfour\nfive\n");
        Write(fork, "Runtime/New.txt", "new\n");
        File.Delete(Path.Combine(fork, "Runtime", "Unused.txt"));
        var manifest = PackageManifest.Load(fork);
        manifest.Upstream = "com.acme.stats@1.0.0 (abc)";
        manifest.Save(fork);

        var differences = BrickFork.Diff(original, fork);

        Assert.Equal(
            [("Runtime/New.txt", ForkChange.Added), ("Runtime/Stats.txt", ForkChange.Modified), ("Runtime/Unused.txt", ForkChange.Removed)],
            differences.Select(d => (d.Path, d.Change)));
    }

    /// <summary>Files the fork left alone follow the new release: updated, added and removed.</summary>
    [Fact]
    public void UntouchedFilesFollowTheRelease()
    {
        Write(release, "Runtime/Stats.txt", "one\ntwo\nthree\nfour\nFIVE\n");
        Write(release, "Runtime/Extra.txt", "extra\n");
        File.Delete(Path.Combine(release, "Runtime", "Unused.txt"));

        var result = BrickFork.Rebase(original, release, fork);

        Assert.Equal(["Runtime/Stats.txt"], result.Updated);
        Assert.Equal(["Runtime/Extra.txt"], result.Added);
        Assert.Equal(["Runtime/Unused.txt"], result.Removed);
        Assert.False(result.HasConflicts);
        Assert.Contains("FIVE", File.ReadAllText(Path.Combine(fork, "Runtime", "Stats.txt")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fork, "Runtime", "Unused.txt")));
    }

    /// <summary>Changes the fork made where the release did not are kept, and edits on different lines merge.</summary>
    [Fact]
    public void ForkChangesSurviveAndDistinctEditsMerge()
    {
        Write(fork, "Runtime/Stats.txt", "ONE\ntwo\nthree\nfour\nfive\n");
        Write(fork, "Runtime/Config.json", "{\n  \"speed\": 9\n}\n");
        Write(release, "Runtime/Stats.txt", "one\ntwo\nthree\nfour\nFIVE\n");

        var result = BrickFork.Rebase(original, release, fork);

        Assert.Equal(["Runtime/Stats.txt"], result.Merged);
        Assert.Empty(result.Conflicts);
        Assert.Equal("ONE\ntwo\nthree\nfour\nFIVE\n", File.ReadAllText(Path.Combine(fork, "Runtime", "Stats.txt")));
        Assert.Contains("9", File.ReadAllText(Path.Combine(fork, "Runtime", "Config.json")), StringComparison.Ordinal);
    }

    /// <summary>Both sides editing one line conflicts, leaving markers in the fork.</summary>
    [Fact]
    public void SameLineEditsConflict()
    {
        Write(fork, "Runtime/Stats.txt", "mine\ntwo\nthree\nfour\nfive\n");
        Write(release, "Runtime/Stats.txt", "theirs\ntwo\nthree\nfour\nfive\n");

        var result = BrickFork.Rebase(original, release, fork);

        Assert.Equal(["Runtime/Stats.txt"], result.Conflicts);
        var text = File.ReadAllText(Path.Combine(fork, "Runtime", "Stats.txt"));
        Assert.Contains("<<<<<<< fork", text, StringComparison.Ordinal);
        Assert.Contains("mine", text, StringComparison.Ordinal);
        Assert.Contains(">>>>>>> upstream", text, StringComparison.Ordinal);
    }

    /// <summary>Binary files both sides changed, and a delete against an edit, are conflicts that keep the fork's state.</summary>
    [Fact]
    public void BinaryAndDeleteAgainstEditConflict()
    {
        File.WriteAllBytes(Path.Combine(fork, "Runtime", "Icon.bin"), [9, 0, 9]);
        File.WriteAllBytes(Path.Combine(release, "Runtime", "Icon.bin"), [7, 0, 7]);
        Write(fork, "Runtime/Unused.txt", "edited by the fork\n");
        File.Delete(Path.Combine(release, "Runtime", "Unused.txt"));

        var result = BrickFork.Rebase(original, release, fork);

        Assert.Equal(["Runtime/Icon.bin", "Runtime/Unused.txt"], result.Conflicts);
        Assert.Equal([9, 0, 9], File.ReadAllBytes(Path.Combine(fork, "Runtime", "Icon.bin")));
        Assert.Equal("edited by the fork\n", File.ReadAllText(Path.Combine(fork, "Runtime", "Unused.txt")));
    }

    string Folder(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, "Runtime"));
        new PackageManifest { Name = "com.acme.stats", Version = SemanticVersion.Parse("1.0.0") }.Save(path);
        return path;
    }

    static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
