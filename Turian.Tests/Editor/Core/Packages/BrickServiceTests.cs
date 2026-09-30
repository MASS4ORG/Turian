namespace Turian.Tests;

/// <summary>What a Turian project does with bricks: create, verify, pack, install.</summary>
public sealed class BrickServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-brick-service-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickServiceTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A new brick passes verification and names its assembly after the id.</summary>
    [Fact]
    public void NewBrickIsSound()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");

        Assert.Empty(BrickVerifier.Verify(folder));
        Assert.True(File.Exists(Path.Combine(folder, "Runtime", "Mateo.Inventory.dataasset")));
        Assert.DoesNotContain("keywords", File.ReadAllText(Path.Combine(folder, "package.json")), StringComparison.Ordinal);
        Assert.Throws<PackageException>(() => BrickService.New(root, "user.mateo.inventory"));
        Assert.Throws<PackageException>(() => BrickService.New(root, "Inventory"));
    }

    /// <summary>Verification names missing metas, duplicate ids and orphans, and ignores <c>~</c> folders.</summary>
    [Fact]
    public void VerifierFindsAssetProblems()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        File.WriteAllText(Path.Combine(folder, "Runtime", "NoMeta.txt"), "x");
        File.WriteAllText(Path.Combine(folder, "Runtime", "Orphan.png.meta"), """{ "Id": "00000000-0000-0000-0000-000000000000" }""");
        var scriptMeta = File.ReadAllText(Path.Combine(folder, "Runtime", "InventoryComponent.cs.meta"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Copy.cs"), "class Copy {}");
        File.WriteAllText(Path.Combine(folder, "Runtime", "Copy.cs.meta"), scriptMeta);
        Directory.CreateDirectory(Path.Combine(folder, "Samples~"));
        File.WriteAllText(Path.Combine(folder, "Samples~", "Free.txt"), "no meta needed");

        var issues = BrickVerifier.Verify(folder);

        Assert.Contains(issues, i => i.Contains("NoMeta.txt has no .meta", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Orphan.png.meta has no asset", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Orphan.png.meta has no valid Id", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("reuses the id", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Contains("Free.txt", StringComparison.Ordinal));
    }

    /// <summary>A brick that fails verification is not packed; a sound one is installed into a project from its file.</summary>
    [Fact]
    public void PackedBricksInstall()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        File.WriteAllText(Path.Combine(folder, "Runtime", "NoMeta.txt"), "x");
        Assert.Throws<PackageException>(() => BrickService.Pack(folder, Path.Combine(root, "out")));
        File.Delete(Path.Combine(folder, "Runtime", "NoMeta.txt"));

        var packed = BrickService.Pack(folder, Path.Combine(root, "out"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));

        var resolution = BrickService.Add(project, "user.mateo.inventory", $"file:{packed.Path}");

        Assert.Equal("user.mateo.inventory", Assert.Single(resolution.Packages).Id);
        Assert.Single(BrickService.List(project));
        Assert.Throws<PackageException>(() => BrickService.Add(project, "user.mateo.missing", "file:../nowhere"));
        Assert.DoesNotContain("user.mateo.missing", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.True(BrickService.Remove(project, "user.mateo.inventory"));
    }

    /// <summary>
    /// A precast brick is consumed as it is: its assemblies are referenced instead of compiled, its types come from
    /// the payload, and a script of the project compiles against it.
    /// </summary>
    [Fact]
    public async Task PrecastBricksAreConsumedWithoutCompiling()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        var packed = await PackPrecast(folder);
        var precast = BrickArchive.ReadManifest(packed, ["turian"]).Precast!;
        Assert.Equal(["Mateo.Inventory"], precast.Assemblies);
        Assert.Equal(ProjectPackages.EngineVersion, precast.BuiltWith[ProjectPackages.HostName]);

        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        var resolution = BrickService.Add(project, "user.mateo.inventory", $"file:{packed}");
        var brick = resolution.Packages.Single(p => p.Id == "user.mateo.inventory");
        File.WriteAllText(Path.Combine(project, "Assets", "Bag.cs"),
            "namespace Usercode; public class Bag : Mateo.Inventory.InventoryComponent { }");

        var settings = new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = project };
        var graph = CsProjectGenerator.DiscoverAssemblies(settings);
        Assert.DoesNotContain(graph.Definitions, a => a.Name == "Mateo.Inventory");
        Assert.Contains("Mateo.Inventory", CsProjectGenerator.GenerateUserCode(settings, NullLogger.Instance, graph).Items
            .Where(i => i.ItemType == "Reference").Select(i => i.Include));

        var types = UserCodeTypeManifestGenerator.Generate(Path.Combine(project, "Assets"), NullLogger.Instance, graph: graph);
        Assert.Contains("Mateo.Inventory", types.PrecastAssemblies);
        var entry = Assert.Single(types.Types, t => t.FullyQualifiedName == "Mateo.Inventory.InventoryComponent");
        Assert.Equal("Mateo.Inventory", entry.Assembly);
        Assert.Equal(entry.TypeId, BrickAssemblies.PrecastTypes(brick).Single().TypeId);

        var (exitCode, output) = await RunCli("compile", project);
        Assert.True(exitCode == 0, output);
    }

    /// <summary>A payload built for another engine version is ignored, so the brick compiles from its sources again.</summary>
    [Fact]
    public async Task PayloadsBuiltForAnotherEngineAreIgnored()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        var built = BrickArchive.ReadManifest(await PackPrecast(folder), ["turian"]).Precast!;
        built.BuiltWith[ProjectPackages.HostName] = new SemanticVersion(ProjectPackages.EngineVersion.Major + 1, 0, 0);
        var packed = BrickService.Pack(folder, Path.Combine(root, "other"), built);

        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        var brick = BrickService.Add(project, "user.mateo.inventory", $"file:{packed.Path}").Packages.Single();

        Assert.False(BrickAssemblies.IsPrecast(brick));
        Assert.Contains(CsProjectGenerator.DiscoverAssemblies(new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = project }).Definitions,
            a => a.Name == "Mateo.Inventory");
    }

    /// <summary>Precasting compiles, so these tests run the command line, which owns the MSBuild registration.</summary>
    async Task<string> PackPrecast(string folder)
    {
        var (exitCode, output) = await RunCli("brick", "pack", folder, "--precast", "--out", Path.Combine(root, "out"));
        Assert.True(exitCode == 0, output);
        return Directory.GetFiles(Path.Combine(root, "out"), "*.brick").Single();
    }

    static async Task<(int ExitCode, string Output)> RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(global::Turian.Editor.CLI.Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[PackageStore.StoreVariable] = Path.Combine(Path.GetTempPath(), "turian-tests-bricks");

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
