namespace Turian.Tests;

/// <summary>Source-built feature modules are loaded even when game code never names their types.</summary>
public sealed class SourceBrickRuntimeManifestTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-source-module-").FullName;

    /// <summary>A source backend is included for a client, while its editor assembly and server-excluded brick stay out.</summary>
    [Theory]
    [InlineData(BuildTarget.Client, true)]
    [InlineData(BuildTarget.Server, false)]
    public void ManifestLoadsOnlyShippingSourceBrickAssemblies(BuildTarget target, bool included)
    {
        var brick = BrickService.New(root, "user.example.runtime");
        var packagePath = Path.Combine(brick, "package.json");
        var package = JsonNode.Parse(File.ReadAllText(packagePath))!.AsObject();
        package["clientOnly"] = true;
        File.WriteAllText(packagePath, package.ToJsonString());
        var editor = Directory.CreateDirectory(Path.Combine(brick, "Editor")).FullName;
        Serializer.Save<DataAsset>(Path.Combine(editor, "Assembly.dataasset"), new AssemblyDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Example.Runtime.Editor",
            EditorOnly = true,
        });
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        BrickService.Add(project, "user.example.runtime", $"file:{brick}");
        var settings = new BuildAppSettings { ProjectAbsoluteDir = project, Title = "Source modules" };
        var graph = CsProjectGenerator.DiscoverAssemblies(settings);
        var backend = Assert.Single(graph.Definitions, static assembly => !assembly.EditorOnly);

        var manifest = UserCodeTypeManifestGenerator.Generate(settings.AssetsAbsoluteDir, NullLogger.Instance,
            settings.TitleToPathFriendly, graph, target);

        Assert.Equal(included, manifest.PrecastAssemblies.Contains(backend.Name));
        Assert.DoesNotContain("Example.Runtime.Editor", manifest.PrecastAssemblies);
        Assert.DoesNotContain(graph.Default.Name, manifest.PrecastAssemblies);
    }

    /// <inheritdoc />
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);
}
