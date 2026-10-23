using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>
/// Baking a brick keeps its import results, and a project installing the brick's bake twin mounts them instead of
/// importing, unless they no longer match the source, its settings or the importer.
/// </summary>
[Trait("Category", "E2E")]
[Collection(SerialTests.Name)]
public sealed class BrickBakeTests : IDisposable
{
    static readonly Guid NotesId = Guid.Parse("4f1c2a7e-9b3d-4e5f-8a6b-1c2d3e4f5a6b");

    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bake-{Guid.NewGuid():N}");
    readonly string? previousStore = Environment.GetEnvironmentVariable(PackageStore.StoreVariable);
    readonly string brick;

    /// <summary>Creates an asset-only brick whose single asset carries a committed meta.</summary>
    public BrickBakeTests()
    {
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable, Path.Combine(root, "store"));
        brick = Path.Combine(root, "notes");
        Directory.CreateDirectory(Path.Combine(brick, "Runtime"));
        new PackageManifest { Name = "user.mateo.notes", Version = SemanticVersion.Parse("1.0.0") }.Save(brick);
        File.WriteAllText(Path.Combine(brick, "Runtime", "notes.txt"), "Twins mount what the baker imported.");
        File.WriteAllText(Path.Combine(brick, "Runtime", "notes.txt.meta"), Serializer.Serialize(new Asset { Id = NotesId }));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable, previousStore);
        PrecastBrickFixture.DeleteTree(root);
    }

    /// <summary>
    /// Packing with a twin bakes first: the twin carries the bake, the source does not, and baking again writes the same
    /// bytes, so a rebuilt twin keeps its integrity.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwinsCarryADeterministicBake(bool v2)
    {
        if (v2)
        {
            File.WriteAllText(Path.Combine(brick, PackageManifest.FileName),
                """{ "id": "user.mateo.notes", "version": "1.0.0", "roles": { "docs": "Brick~/Documentation" } }""");
        }

        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "pack", brick, "--twin", "--out", Path.Combine(root, "out"));
        Assert.True(exitCode == 0, output);

        var relative = v2 ? "Brick~/Bake" : BrickBake.Folder;
        var bake = Path.Combine(brick, relative);
        var sync = JsonSerializer.Deserialize<BakeSync>(File.ReadAllText(Path.Combine(bake, BrickBake.SyncFileName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var entry = Assert.Single(sync.Entries);
        Assert.Equal(("Runtime/notes.txt", NotesId), (entry.Path, entry.Guid));
        Assert.NotEmpty(entry.Artifacts);

        using (var source = ZipFile.OpenRead(Directory.GetFiles(Path.Combine(root, "out"), "user.mateo.notes-*.brick").Single()))
        using (var twin = ZipFile.OpenRead(Directory.GetFiles(Path.Combine(root, "out"), "user.mateo.notes.bake-*.brick").Single()))
        {
            Assert.DoesNotContain(source.Entries, e => e.FullName.StartsWith(relative, StringComparison.Ordinal));
            Assert.Contains(twin.Entries, e => e.FullName == $"{relative}/{BrickBake.SyncFileName}");
        }

        var first = Snapshot(bake);
        (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "bake", brick);
        Assert.True(exitCode == 0, output);
        Assert.Equal(first, Snapshot(bake));
    }

    /// <summary>
    /// A project installing only the twin mounts the baked import, without importing; a bake whose settings no longer
    /// match is imported instead, with a warning.
    /// </summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("settings")]
    [InlineData("artifact")]
    [InlineData("importer")]
    public async Task TwinsMountTheirBakeUnlessItIsStale(string problem)
    {
        var stale = problem != "valid";
        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "bake", brick);
        Assert.True(exitCode == 0, output);
        if (problem == "settings")
        {
            var import = Path.Combine(brick, BrickBake.Folder, BrickBake.ImportedFolder, NotesId.ToString("N"), "import.json");
            File.WriteAllText(import, File.ReadAllText(import).Replace("\"SettingsHash\": \"", "\"SettingsHash\": \"0", StringComparison.Ordinal));
        }

        if (problem == "artifact")
        {
            File.WriteAllText(Path.Combine(brick, BrickBake.Folder, BrickBake.ImportedFolder, NotesId.ToString("N"), "notes.txt"), "Changed after baking.");
        }

        if (problem == "importer")
        {
            var import = Path.Combine(brick, BrickBake.Folder, BrickBake.ImportedFolder, NotesId.ToString("N"), "import.json");
            File.WriteAllText(import, File.ReadAllText(import).Replace("GenericAssetImporter", "AnotherImporter", StringComparison.Ordinal));
        }

        var (_, twin) = BrickService.PackWithTwin(brick, Path.Combine(root, "out"));
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")))!;
        _ = BrickService.Add(project, twin.Manifest.Name, $"file:{twin.Path}");

        (exitCode, output) = await PrecastBrickFixture.RunCli(root, "scene", project, "--reimport");

        Assert.True(exitCode == 0, output);
        Assert.Equal(stale, output.Contains($"Imported asset {NotesId}", StringComparison.Ordinal));
        Assert.Equal(!stale, output.Contains("Mounted 1 baked import(s)", StringComparison.Ordinal));
        Assert.Equal(stale, output.Contains("does not match its source, settings or importer", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(AssetImporter.ImportDirectoryOf(project, NotesId), "import.json")));
    }

    /// <summary>Where an asset is does not change its import settings, so imports survive a moved project or brick.</summary>
    [Fact]
    public void ImportSettingsDoNotDependOnTheAssetsLocation()
    {
        var here = new Asset { Id = NotesId, RelativePath = "/home/mateo/game/Assets/notes.txt" };
        var there = new Asset { Id = NotesId, RelativePath = "/builds/game/Assets/notes.txt" };

        Assert.Equal(AssetImporter.ImportSettingsOf(here), AssetImporter.ImportSettingsOf(there));
    }

    /// <summary>Disjoint baked assets in separate role folders both mount without reimporting.</summary>
    [Fact]
    public async Task EveryBakeRoleFolderContributesItsAssets()
    {
        var otherId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(brick, "Runtime", "other.txt"), "A separate baked payload.");
        File.WriteAllText(Path.Combine(brick, "Runtime", "other.txt.meta"), Serializer.Serialize(new Asset { Id = otherId }));
        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "bake", brick);
        Assert.True(exitCode == 0, output);

        var first = Path.Combine(brick, "Brick~", "First");
        var second = Path.Combine(brick, "Brick~", "Second");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.Move(Path.Combine(brick, BrickBake.Folder), first);
        Directory.CreateDirectory(Path.Combine(second, BrickBake.ImportedFolder));
        Directory.Move(Path.Combine(first, BrickBake.ImportedFolder, otherId.ToString("N")),
            Path.Combine(second, BrickBake.ImportedFolder, otherId.ToString("N")));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var syncPath = Path.Combine(first, BrickBake.SyncFileName);
        var sync = JsonSerializer.Deserialize<BakeSync>(File.ReadAllText(syncPath), options)!;
        var other = sync.Entries.Single(entry => entry.Guid == otherId);
        sync.Entries.Remove(other);
        File.WriteAllText(syncPath, JsonSerializer.Serialize(sync, options));
        sync.Entries = [other];
        File.WriteAllText(Path.Combine(second, BrickBake.SyncFileName), JsonSerializer.Serialize(sync, options));
        var manifest = PackageManifest.Load(brick);
        manifest.UsesId = true;
        manifest.Roles[BrickLayout.Bake] = JsonSerializer.SerializeToElement(new[] { "Brick~/First", "Brick~/Second" });
        manifest.Save(brick);

        var (_, twin) = BrickService.PackWithTwin(brick, Path.Combine(root, "out"));
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")))!;
        _ = BrickService.Add(project, twin.Manifest.Name, $"file:{twin.Path}");
        (exitCode, output) = await PrecastBrickFixture.RunCli(root, "scene", project, "--reimport");
        Assert.True(exitCode == 0, output);
        Assert.Contains("Mounted 2 baked import(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"Imported asset {NotesId}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"Imported asset {otherId}", output, StringComparison.Ordinal);
    }

    /// <summary>A missing development link remains an explicit import error rather than resolving from a registry.</summary>
    [Fact]
    public async Task ImportReportsMissingProjectLinks()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "missing-link")))!;
        var (manifest, _) = ProjectManifest.Load(project);
        manifest.Links["user.mateo.missing"] = "../../missing-checkout";
        manifest.Save(project);
        ProjectPackages.Invalidate(project);
        var settings = SettingsService.Load(project)!;
        var settingsService = new SettingsService();
        settingsService.Set(settings);
        var logger = Substitute.For<ILogger>();
        using var importer = new AssetImporter(logger, new AssetDatabase(), settingsService);
        importer.GenerateMetaFiles(settings.AssetsAbsoluteDir);
        Assert.Contains(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && call.GetArguments()[0] is LogLevel.Error && call.GetArguments()[3] is PackageException error
            && error.Message.Contains("linked folder", StringComparison.Ordinal));
    }

    /// <summary>A failed source compilation is reported and cannot leave a publishable archive behind.</summary>
    [Fact]
    public async Task BrokenSourcesCannotProduceAnArchive()
    {
        var folder = BrickService.New(root, "user.mateo.broken");
        var (initialCode, initialOutput) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder,
            "--precast", "--out", Path.Combine(root, "initial-output"));
        Assert.True(initialCode == 0, initialOutput);
        Assert.True(Directory.Exists(Path.Combine(folder, "Brick~", "Precast")));
        File.WriteAllText(Path.Combine(folder, "Runtime", "BrokenComponent.cs"),
            "namespace Mateo.Broken; public class BrokenComponent : MissingBaseType { }");
        var outputDirectory = Path.Combine(root, "failed-output");
        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder,
            "--precast", "--out", outputDirectory);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("does not compile", output, StringComparison.Ordinal);
        Assert.True(!Directory.Exists(outputDirectory) || !Directory.EnumerateFiles(outputDirectory, "*.brick").Any());
        Assert.False(Directory.Exists(Path.Combine(folder, "Brick~", "Precast")));
    }

    /// <summary>Consumer-built editor assemblies can use the editor import contract without an engine checkout.</summary>
    [Fact]
    public async Task EditorSourcesCompileAgainstTheEditorContract()
    {
        var folder = BrickService.New(root, "user.mateo.importer");
        var manifest = PackageManifest.Load(folder, ["turian"]);
        manifest.Roles.Remove(BrickLayout.Precast);
        manifest.Save(folder);
        var definitionPath = Path.Combine(folder, "Runtime", "Mateo.Importer.dataasset");
        var definition = Serializer.Load<AssemblyDefinition>(definitionPath)!;
        definition.EditorOnly = true;
        definition.EmbeddedResources.Add("Resources~/notes.txt");
        Serializer.Save(definitionPath, definition);
        Assert.Contains("Resources~/notes.txt", File.ReadAllText(definitionPath), StringComparison.Ordinal);
        Assert.Equal(["Resources~/notes.txt"], Serializer.Load<AssemblyDefinition>(definitionPath)!.EmbeddedResources);
        Directory.CreateDirectory(Path.Combine(folder, "Runtime", "Resources~"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Resources~", "notes.txt"), "Native payload stays embedded.");
        Assert.Equal(["Resources~/notes.txt"], AssemblyGraph.Discover(folder, "Game").Definitions.Single().EmbeddedResources);
        File.WriteAllText(Path.Combine(folder, "Runtime", "ImporterComponent.cs"),
            "namespace Mateo.Importer; public class ImporterComponent { public System.Type Contract => typeof(Turian.Editor.Core.IAssetImporter); }");

        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder, "--precast", "--twin", "--out", Path.Combine(root, "compiled"));

        Assert.True(exitCode == 0, output);
        var assemblyPath = Path.Combine(folder, "Brick~", "Precast", "editor", "Mateo.Importer.dll");
        Assert.True(File.Exists(assemblyPath));
        var context = new AssemblyLoadContext("brick-resource", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            Assert.Contains("Mateo.Importer.Resources~.notes.txt", assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream("Mateo.Importer.Resources~.notes.txt")!;
            using var reader = new StreamReader(stream);
            Assert.Equal("Native payload stays embedded.", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            context.Unload();
        }
    }

    static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(folder, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
}
