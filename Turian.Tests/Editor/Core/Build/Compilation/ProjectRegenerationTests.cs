namespace Turian.Tests;

/// <summary>Regeneration writes the projects and solution an external IDE opens, and only when they changed.</summary>
public sealed class ProjectRegenerationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-regenerate-").FullName;

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    async Task<BuildAppSettings> CreateProject()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game é")))!;
        var settings = (BuildAppSettings)new BuildAppSettings().Load(SettingsService.Load(project)!);
        ProjectSettingsLoader.LoadFromSources(settings);
        return settings;
    }

    void Define(BuildAppSettings settings, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(settings.AssetsAbsoluteDir, name)).FullName;
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(folder, $"{name}.dataasset"), $$"""
            { "__TypeId": "{{AssemblyDefinition.TypeIdValue}}", "Name": "Sample.{{name}}", "AutoReferenced": false,
              "Id": "{{id}}" }
            """);
        File.WriteAllText(Path.Combine(folder, $"{name}.dataasset.meta"), $$"""
            { "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c", "RelativePath": "Assets/{{name}}/{{name}}.dataasset",
              "Id": "{{id}}" }
            """);
        File.WriteAllText(Path.Combine(folder, "Value.cs"), $"namespace Sample.{name}; public static class Value;");
    }

    /// <summary>The default project and the solution listing it appear where an IDE can find them, with relative paths.</summary>
    [Fact]
    public async Task WritesTheSolutionAndProjects()
    {
        var settings = await CreateProject();

        var files = ProjectRegeneration.Regenerate(settings, NullLogger.Instance);

        Assert.True(files.Changed);
        Assert.Equal(ProjectRegeneration.SolutionPath(settings), files.Solution);
        Assert.StartsWith(settings.CacheAbsoluteDir, files.Solution, StringComparison.Ordinal);
        var project = Assert.Single(files.Projects);
        Assert.True(File.Exists(project));
        var listed = Assert.Single(XDocument.Load(files.Solution).Root!.Elements("Project"));
        Assert.Equal(project, Path.GetFullPath((string)listed.Attribute("Path")!, Path.GetDirectoryName(files.Solution)!));
        Assert.DoesNotContain('\\', (string)listed.Attribute("Path")!);
    }

    /// <summary>Running it again with nothing changed leaves every file, and so its timestamp, untouched.</summary>
    [Fact]
    public async Task SecondRunLeavesFilesUntouched()
    {
        var settings = await CreateProject();
        var first = ProjectRegeneration.Regenerate(settings, NullLogger.Instance);
        var files = first.Projects.Append(first.Solution).ToList();
        var past = DateTime.UtcNow.AddDays(-1);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, past);
        }

        var second = ProjectRegeneration.Regenerate(settings, NullLogger.Instance);

        Assert.False(second.Changed);
        Assert.Equal(first.Projects, second.Projects);
        Assert.All(files, file => Assert.Equal(past, File.GetLastWriteTimeUtc(file)));
    }

    /// <summary>Adding an assembly definition adds its project to the solution; nothing else in the project is written.</summary>
    [Fact]
    public async Task AssemblyDefinitionChangesAreReflectedAndUnrelatedFilesUntouched()
    {
        var settings = await CreateProject();
        ProjectRegeneration.Regenerate(settings, NullLogger.Instance);
        List<string> assetsBefore = [.. Directory.GetFiles(settings.AssetsAbsoluteDir, "*", SearchOption.AllDirectories).Order()];
        Define(settings, "Inventory");
        List<string> assetsAfterDefine = [.. Directory.GetFiles(settings.AssetsAbsoluteDir, "*", SearchOption.AllDirectories).Order()];

        var files = ProjectRegeneration.Regenerate(settings, NullLogger.Instance);

        Assert.True(files.Changed);
        Assert.Equal(2, files.Projects.Count);
        Assert.Contains(files.Projects, static project => project.EndsWith("Sample.Inventory.csproj", StringComparison.Ordinal));
        Assert.Equal(2, XDocument.Load(files.Solution).Root!.Elements("Project").Count());
        Assert.NotEqual(assetsBefore, assetsAfterDefine);
        Assert.Equal(assetsAfterDefine,
            [.. Directory.GetFiles(settings.AssetsAbsoluteDir, "*", SearchOption.AllDirectories).Order()]);
    }

    /// <summary>The CLI prints the generated paths, and a second run prints the same ones and rewrites nothing.</summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task CliPrintsTheGeneratedPathsAndIsIdempotent()
    {
        var settings = await CreateProject();

        var first = await PrecastBrickFixture.RunCli(root, "regenerate", settings.ProjectAbsoluteDir);
        Assert.True(first.ExitCode == 0, first.Output);
        var solution = ProjectRegeneration.SolutionPath(settings);
        Assert.Contains(solution, first.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(solution));
        var past = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(solution, past);

        var second = await PrecastBrickFixture.RunCli(root, "regenerate", settings.ProjectAbsoluteDir);

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("already up to date", second.Output, StringComparison.Ordinal);
        Assert.Equal(past, File.GetLastWriteTimeUtc(solution));
    }

    /// <summary>The solution lists projects relative to itself with forward slashes, so it reads the same on every OS.</summary>
    [Fact]
    public void SolutionXmlUsesRelativeForwardSlashPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sln dir é");
        var xml = ProjectRegeneration.SolutionXml(Path.Combine(directory, "Game.slnx"),
            [Path.Combine(directory, "Source", "Game.csproj"), Path.Combine(directory, "Assemblies", "A B", "A B.csproj")]);

        Assert.Equal(["Source/Game.csproj", "Assemblies/A B/A B.csproj"],
            XDocument.Parse(xml).Root!.Elements("Project").Select(static e => (string)e.Attribute("Path")!));
    }
}
