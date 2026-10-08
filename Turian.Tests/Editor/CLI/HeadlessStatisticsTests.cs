using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Exercises headless reporting with and without graphics and PNG output.</summary>
[Collection(SerialTests.Name)]
public sealed class HeadlessStatisticsTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>The CLI Stats option produces geometry measurements without requiring PNG output.</summary>
    [Fact]
    public async Task StatisticsOptionEnablesRenderingWithoutPng()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var directory = Directory.CreateTempSubdirectory("turian-statistics-command-");
        try
        {
            var assets = Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            var path = Path.Combine(assets.FullName, "scene.prefab");
            Serializer.Save(path, new Node { Name = "Command fixture" });
            var scene = new Prefab { RelativePath = path };
            var database = new AssetDatabase();
            Assert.True(database.RegisterAsset(scene));
            database.SaveCatalog(directory.FullName);
            var argument = new System.CommandLine.Argument<FileSystemInfo>("ProjectPath");
            var factory = typeof(global::Turian.Editor.CLI.Program)
                .GetMethod("PlayModeCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
            var command = (System.CommandLine.Command)factory.Invoke(null, [argument])!;
            var root = new System.CommandLine.RootCommand { command };
            var report = Path.Combine(directory.FullName, "stats.json");
            var exitCode = await root.Parse(["playmode", directory.FullName, "--scene", scene.Id.ToString(),
                "--frames", "3", "--width", "32", "--height", "32", "--stats", report])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal(3, json.RootElement.GetProperty("frames").GetInt32());
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("samples")[0]
                .GetProperty("rendering").ValueKind);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>JSON captures actual frame timings and scene loading independently of final-image output.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReportsFramesWithOptionalRenderingAndPng(bool graphics, bool png)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var directory = Directory.CreateTempSubdirectory("turian-headless-statistics-");
        HeadlessProject? project = null;
        try
        {
            var assetDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            var scenePath = Path.Combine(assetDirectory.FullName, "scene.prefab");
            Serializer.Save(scenePath, new Node { Name = "Statistics fixture" });
            var scene = new Prefab { RelativePath = scenePath };
            var database = new AssetDatabase();
            Assert.True(database.RegisterAsset(scene));
            database.SaveCatalog(directory.FullName);
            var settings = new BuildAppSettings
            {
                ProjectAbsoluteDir = directory.FullName,
                Title = "Statistics fixture",
            };
            project = HeadlessProject.Open(settings, NullLogger.Instance, withGraphics: graphics);
            var root = project.LoadScene(scene.Id.ToString());
            var reportPath = Path.Combine(directory.FullName, "frames.json");
            var imagePath = png ? Path.Combine(directory.FullName, "frame.png") : null;
            var options = new ScreenshotOptions
            {
                Width = 32,
                Height = 32,
                Frames = 3,
                Yaw = 0,
                Pitch = 0,
                Headlight = 0,
            };
            Assert.True(HeadlessPlay.Run(project, root, 3, imagePath, options, default, null,
                NullLogger.Instance, reportPath));
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(3, report.RootElement.GetProperty("frames").GetInt32());
            Assert.True(report.RootElement.GetProperty("sceneLoadMilliseconds").GetDouble() > 0);
            var first = report.RootElement.GetProperty("samples")[0];
            Assert.True(first.GetProperty("frameMilliseconds").GetDouble() > 0);
            if (graphics)
            {
                Assert.Equal(0, first.GetProperty("rendering").GetProperty("drawCalls").GetInt32());
                Assert.True(first.GetProperty("rendering").GetProperty("submitMilliseconds").GetDouble() > 0);
            }
            else Assert.Equal(JsonValueKind.Null, first.GetProperty("rendering").ValueKind);
            if (png) Assert.True(File.Exists(imagePath));
            Assert.True(HeadlessPlay.Run(project, root, 1, null, options, default, null, NullLogger.Instance));
        }
        finally
        {
            project?.Dispose();
            project?.Vulkan?.Device.Dispose();
            directory.Delete(true);
        }
    }
}
