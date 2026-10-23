namespace Turian.Tests;

/// <summary>Checks that source brick components remain available while headless scenes deserialize.</summary>
[Trait("Category", "E2E")]
[Collection(SerialTests.Name)]
public sealed class HeadlessBrickTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-headless-brick-").FullName;

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>Loading user code preserves a brick's component type through scene inspection, with either import mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SceneInspectionKeepsSourceBrickComponentsLoaded(bool reimport)
    {
        var (project, scene) = await CreateProjectAsync();
        var baseline = await PrecastBrickFixture.RunCli(root, "scene", project, "--scene", scene, "--reimport");
        Assert.True(baseline.ExitCode == 0, baseline.Output);
        Assert.Contains("MissingComponent: 1", baseline.Output, StringComparison.Ordinal);

        string[] arguments =
            ["scene", project, "--scene", scene, "--load-usercode", .. reimport ? (string[])["--reimport"] : []];
        var loaded = await PrecastBrickFixture.RunCli(root, arguments);

        Assert.True(loaded.ExitCode == 0, loaded.Output);
        Assert.Contains("HeadlessComponent: 1", loaded.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("MissingComponent", loaded.Output, StringComparison.Ordinal);
    }

    /// <summary>The preparation owner keeps source types registered until scene processing completes.</summary>
    [Fact]
    public async Task PreparationLifetimeOwnsLoadedSourceTypes()
    {
        var (project, scene) = await CreateProjectAsync();
        var settings = new BuildAppSettings { ProjectAbsoluteDir = project, Title = "Headless brick" };
        ProjectSettingsLoader.LoadFromSources(settings);
        Assert.Null(await global::Turian.Editor.CLI.Program.PrepareProjectAsync(settings,
            reimport: false, loadUserCode: false));
        using var preparation = await global::Turian.Editor.CLI.Program.PrepareProjectAsync(settings,
            reimport: true, loadUserCode: true);
        Assert.NotNull(preparation);
        Assert.True(preparation.IsAssemblyLoaded);
        using (var headless = global::Turian.Editor.CLI.HeadlessProject.Open(settings,
                   NullLogger.Instance, withGraphics: false))
        {
            var component = Assert.Single(headless.LoadScene(scene).Components);
            Assert.Equal("Mateo.Headless.HeadlessComponent", component.GetType().FullName);
        }

        preparation.Dispose();
        Assert.False(preparation.IsAssemblyLoaded);
        Assert.False(TypeRegistry.TryGetType("Mateo.Headless.HeadlessComponent", out _));
    }

    /// <summary>A failed compilation leaves the diagnostic host able to report unavailable component placeholders.</summary>
    [Fact]
    public async Task FailedCompilationLeavesSourceComponentsUnavailable()
    {
        var (project, scene) = await CreateProjectAsync();
        File.WriteAllText(Path.Combine(project, "Assets", "Game.cs"), "invalid C# source");
        var settings = new BuildAppSettings { ProjectAbsoluteDir = project, Title = "Broken headless brick" };
        ProjectSettingsLoader.LoadFromSources(settings);

        using var preparation = await global::Turian.Editor.CLI.Program.PrepareProjectAsync(settings,
            reimport: true, loadUserCode: true);

        Assert.NotNull(preparation);
        Assert.False(preparation.IsAssemblyLoaded);
        using var headless = global::Turian.Editor.CLI.HeadlessProject.Open(settings,
            NullLogger.Instance, withGraphics: false);
        Assert.IsType<MissingComponent>(Assert.Single(headless.LoadScene(scene).Components));
    }

    async Task<(string Project, string Scene)> CreateProjectAsync()
    {
        var brick = BrickService.New(root, "user.mateo.headless");
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(brick, "Runtime", "HeadlessComponent.cs.meta")));
        var typeId = meta.RootElement.GetProperty("Id").GetGuid();
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        BrickService.Add(project, "user.mateo.headless", $"file:{brick}");
        var scene = Path.Combine(project, "Assets", "headless.prefab");
        File.WriteAllText(scene, $$"""
            {
              "__TypeId": "f7cc675d-54f2-510b-8e5d-807281e3012d",
              "Name": "Brick scene",
              "Components": [{ "__TypeId": "{{typeId}}", "Id": "{{Guid.NewGuid()}}" }]
            }
            """);

        return (project, scene);
    }
}
