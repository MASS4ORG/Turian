using Gaya.Packages;

namespace Turian.Tests;

/// <summary>Stubs checked against the real brick, and embedded forks compared with and merged into new releases.</summary>
public sealed class BrickTeamFlowTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-team-flow-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickTeamFlowTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A stub keeps every id and type of the real brick and replaces heavy content with placeholders.</summary>
    [Fact]
    public void StubsCoverTheRealBrick()
    {
        var real = RealBrick();
        var stub = Path.Combine(root, "stub");

        var version = BrickStub.Write(real, stub);

        Assert.Equal("1.2.0-stub", version);
        Assert.Empty(BrickVerifier.VerifyAgainst(stub, real));
        Assert.Equal(0, new FileInfo(Path.Combine(stub, "Runtime", "Level.bin")).Length);
        Assert.True(new FileInfo(Path.Combine(stub, "Runtime", "Icon.png")).Length is > 0 and < 200);
        var script = File.ReadAllText(Path.Combine(stub, "Runtime", "Door.cs"));
        Assert.Contains("namespace Mateo.Levels;", script, StringComparison.Ordinal);
        Assert.Contains("public class Door : Turian.Engine.Core.Component", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Open()", script, StringComparison.Ordinal);
        Assert.Throws<PackageException>(() => BrickStub.Write(real, stub));
    }

    /// <summary>A stub that lost an asset, changed its kind or renamed a type is reported against the real brick.</summary>
    [Fact]
    public void DriftedStubsAreReported()
    {
        var real = RealBrick();
        var stub = Path.Combine(root, "stub");
        BrickStub.Write(real, stub);

        File.Delete(Path.Combine(stub, "Runtime", "Level.bin"));
        File.Delete(Path.Combine(stub, "Runtime", "Level.bin.meta"));
        var iconMeta = Path.Combine(stub, "Runtime", "Icon.png.meta");
        File.WriteAllText(iconMeta, File.ReadAllText(iconMeta).Replace("a3000000-0000-4000-8000-00000000000b", "a3000000-0000-4000-8000-000000000003", StringComparison.Ordinal));
        var script = Path.Combine(stub, "Runtime", "Door.cs");
        File.WriteAllText(script, File.ReadAllText(script).Replace("class Door", "class Gate", StringComparison.Ordinal));

        var issues = BrickVerifier.VerifyAgainst(stub, real);

        Assert.Contains(issues, i => i.Contains("Level.bin) is missing", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Icon.png", StringComparison.Ordinal) && i.Contains("expected a3000000-0000-4000-8000-00000000000b", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Mateo.Levels.Gate, expected Mateo.Levels.Door", StringComparison.Ordinal));
    }

    /// <summary>The real brick can be a packed file: a stub is checked against what was published.</summary>
    [Fact]
    public void StubsAreCheckedAgainstBrickFiles()
    {
        var real = RealBrick();
        var packed = BrickService.Pack(real, Path.Combine(root, "out"));
        var stub = Path.Combine(root, "stub");
        BrickStub.Write(real, stub);

        Assert.Empty(BrickVerifier.VerifyAgainst(stub, packed.Path));
    }

    /// <summary>Embedding a brick from a file, editing it, and rebasing onto a new file keeps the edit and takes the release.</summary>
    [Fact]
    public void ForksRebaseOntoNewBrickFiles()
    {
        var real = RealBrick();
        var v1 = BrickService.Pack(real, Path.Combine(root, "out")).Path;
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        BrickService.Add(project, "user.mateo.levels", $"file:{v1}");
        var fork = BrickService.Embed(project, "user.mateo.levels");
        Assert.Empty(BrickService.Diff(project, "user.mateo.levels"));

        File.WriteAllText(Path.Combine(fork, "Runtime", "Notes.txt"), "fork header\nline one\nline two\nline three\n");
        Assert.Equal([("Runtime/Notes.txt", ForkChange.Modified)], BrickService.Diff(project, "user.mateo.levels").Select(d => (d.Path, d.Change)));
        File.WriteAllText(Path.Combine(fork, "Runtime", "Mine.txt"), "mine\n");
        Meta(Path.Combine(fork, "Runtime", "Mine.txt"));

        File.WriteAllText(Path.Combine(real, "Runtime", "Notes.txt"), "line one\nline two\nline three\nadded upstream\n");
        var manifest = PackageManifest.Load(real, ["turian"]);
        manifest.Version = SemanticVersion.Parse("1.3.0");
        manifest.Save(real);
        var v2 = BrickService.Pack(real, Path.Combine(root, "out")).Path;

        var result = BrickService.Rebase(project, "user.mateo.levels", $"file:{v2}");

        Assert.False(result.HasConflicts);
        Assert.Equal(["Runtime/Notes.txt"], result.Merged);
        Assert.Equal("fork header\nline one\nline two\nline three\nadded upstream\n", File.ReadAllText(Path.Combine(fork, "Runtime", "Notes.txt")));
        Assert.True(File.Exists(Path.Combine(fork, "Runtime", "Mine.txt")));
        Assert.StartsWith("user.mateo.levels@1.3.0 (sha256-", PackageManifest.Load(fork, ["turian"]).Upstream, StringComparison.Ordinal);
        Assert.Equal($"file:{v2}", ProjectManifest.Load(project).Manifest.Dependencies["user.mateo.levels"]);
        Assert.Equal([("Runtime/Mine.txt", ForkChange.Added), ("Runtime/Mine.txt.meta", ForkChange.Added), ("Runtime/Notes.txt", ForkChange.Modified)],
            BrickService.Diff(project, "user.mateo.levels").Select(d => (d.Path, d.Change)));
    }

    /// <summary>A fork of a git brick compares against the commit it was copied from, not the newest one.</summary>
    [Fact]
    public void GitForksComparedAgainstTheirPinnedCommit()
    {
        var repository = RealBrick();
        Git(repository, "init", "--quiet", "--initial-branch=main");
        Git(repository, "add", "-A");
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "-m", "v1");
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        BrickService.Add(project, "user.mateo.levels", $"git+{repository}");
        var fork = BrickService.Embed(project, "user.mateo.levels");

        File.WriteAllText(Path.Combine(repository, "Runtime", "Later.txt"), "later\n");
        Meta(Path.Combine(repository, "Runtime", "Later.txt"));
        Git(repository, "add", "-A");
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "-m", "v2");

        Assert.Empty(BrickService.Diff(project, "user.mateo.levels"));
        var result = BrickService.Rebase(project, "user.mateo.levels");
        Assert.Equal(["Runtime/Later.txt", "Runtime/Later.txt.meta"], result.Added.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(fork, "Runtime", "Later.txt")));
    }

    string RealBrick()
    {
        var folder = BrickService.New(root, "user.mateo.levels");
        File.Delete(Path.Combine(folder, "Runtime", "LevelsComponent.cs"));
        File.Delete(Path.Combine(folder, "Runtime", "LevelsComponent.cs.meta"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Door.cs"), "namespace Mateo.Levels;\npublic class Door : Turian.Engine.Core.Component\n{\n    public void Open() { }\n}\n");
        Meta(Path.Combine(folder, "Runtime", "Door.cs"), "a3000000-0000-4000-8000-000000000003");
        File.WriteAllBytes(Path.Combine(folder, "Runtime", "Level.bin"), [1, 2, 3, 4, 5, 6, 7, 8]);
        Meta(Path.Combine(folder, "Runtime", "Level.bin"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Notes.txt"), "line one\nline two\nline three\n");
        Meta(Path.Combine(folder, "Runtime", "Notes.txt"));
        using (var bitmap = new SkiaSharp.SKBitmap(32, 32))
        using (var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(folder, "Runtime", "Icon.png"), png.ToArray());
        Meta(Path.Combine(folder, "Runtime", "Icon.png"), "a3000000-0000-4000-8000-00000000000b");
        var manifest = PackageManifest.Load(folder, ["turian"]);
        manifest.Version = SemanticVersion.Parse("1.2.0");
        manifest.Save(folder);
        return folder;
    }

    static void Meta(string assetPath, string typeId = "a3000000-0000-4000-8000-000000000003") =>
        File.WriteAllText($"{assetPath}.meta", $$"""{ "__TypeId": "{{typeId}}", "RelativePath": "{{Path.GetFileName(assetPath)}}", "Id": "{{Guid.NewGuid()}}" }""");

    static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
