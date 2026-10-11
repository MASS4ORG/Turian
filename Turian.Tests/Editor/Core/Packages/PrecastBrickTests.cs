namespace Turian.Tests;

/// <summary>
/// One brick packed with precompiled assemblies, shared by <see cref="PrecastBrickTests"/>: precasting runs a full
/// compile through the command line, so it happens once per class.
/// </summary>
public sealed class PrecastBrickFixture : IAsyncLifetime
{
    /// <summary>The scratch folder holding the brick sources, the packed brick and the command line's store.</summary>
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"turian-precast-{Guid.NewGuid():N}");

    /// <summary>The brick's source folder.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>The packed brick, with its precast payload.</summary>
    public string Packed { get; private set; } = "";

    /// <summary>Creates and precasts the brick.</summary>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        Folder = BrickService.New(Root, "user.mateo.inventory");
        var (exitCode, output) = await RunCli(Root, "brick", "pack", Folder, "--precast", "--out", Path.Combine(Root, "out"));
        Assert.True(exitCode == 0, output);
        Packed = Directory.GetFiles(Path.Combine(Root, "out"), "*.brick").Single();
    }

    /// <summary>Deletes the scratch folder.</summary>
    public ValueTask DisposeAsync()
    {
        DeleteTree(Root);
        return ValueTask.CompletedTask;
    }

    /// <summary>Deletes a folder, clearing the read-only flags packing leaves on files.</summary>
    /// <param name="path">The folder to delete.</param>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Runs the command line, which owns the MSBuild registration precasting needs. Its brick store lives under
    /// <paramref name="root"/>, so it is deleted with it.
    /// </summary>
    /// <param name="root">The scratch folder for the store.</param>
    /// <param name="arguments">The command line arguments.</param>
    /// <returns>The exit code and the combined output.</returns>
    public static async Task<(int ExitCode, string Output)> RunCli(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(global::Turian.Editor.CLI.Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[PackageStore.StoreVariable] = Path.Combine(root, "store");

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}

/// <summary>How a project consumes a brick that ships precompiled assemblies.</summary>
/// <param name="fixture">The precast brick shared by the class.</param>
[Trait("Category", "E2E")]
public sealed class PrecastBrickTests(PrecastBrickFixture fixture) : IClassFixture<PrecastBrickFixture>, IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-precast-project-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>
    /// A precast brick is consumed as it is: its assemblies are referenced instead of compiled, its types come from
    /// the payload, and a script of the project compiles against it.
    /// </summary>
    [Fact]
    public async Task PrecastBricksAreConsumedWithoutCompiling()
    {
        var precast = BrickArchive.ReadManifest(fixture.Packed, ["turian"]).Precast!;
        Assert.Equal(["Mateo.Inventory"], precast.Assemblies);
        Assert.Equal(ProjectPackages.EngineVersion, precast.BuiltWith[ProjectPackages.HostName]);

        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        var resolution = BrickService.Add(project, "user.mateo.inventory", $"file:{fixture.Packed}");
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

        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "compile", project);
        Assert.True(exitCode == 0, output);
    }

    /// <summary>A payload built for another engine version is ignored, so the brick compiles from its sources again.</summary>
    [Fact]
    public void PayloadsBuiltForAnotherEngineAreIgnored()
    {
        var built = BrickArchive.ReadManifest(fixture.Packed, ["turian"]).Precast!;
        built.BuiltWith[ProjectPackages.HostName] = new SemanticVersion(ProjectPackages.EngineVersion.Major + 1, 0, 0);
        var packed = BrickService.Pack(fixture.Folder, Path.Combine(root, "other"), built);

        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        var brick = BrickService.Add(project, "user.mateo.inventory", $"file:{packed.Path}").Packages.Single();

        Assert.False(BrickAssemblies.IsPrecast(brick));
        Assert.Contains(CsProjectGenerator.DiscoverAssemblies(new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = project }).Definitions,
            a => a.Name == "Mateo.Inventory");
    }
}
