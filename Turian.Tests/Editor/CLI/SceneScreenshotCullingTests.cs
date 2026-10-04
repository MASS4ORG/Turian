using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Verifies headless screenshots report the submeshes actually submitted to their view.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneScreenshotCullingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>A screenshot logs its culling counts and writes a PNG while cleaning up its temporary headlight.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenshotReportsItsCullingCounts(bool useOcclusion)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = new Node();
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        foreach (var x in new[] { 0f, 1000f })
        {
            var node = new Node { Position = new Vector3(x, 0f, 4f), Parent = root };
            node.AddComponent(new ModelComponent { ModelOverride = quad });
            root.Children.Add(node);
        }

        var options = new ScreenshotOptions
        {
            Width = 32,
            Height = 32,
            Frames = 2,
            Position = Vector3.Zero,
            Yaw = 0f,
            Pitch = 0f,
            Headlight = 20f,
        };
        var logger = Substitute.For<ILogger>();
        var directory = Directory.CreateTempSubdirectory("turian-culling-shot-");
        try
        {
            var database = new AssetDatabase();
            if (useOcclusion)
            {
                TypeRegistry.ScanAssembly(typeof(global::Turian.Engine.Hzb.HzbSettings).Assembly);
                var settings = new global::Turian.Engine.Hzb.HzbSettings { Id = Guid.NewGuid(), Enabled = true };
                Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
                var path = Path.Combine(directory.FullName, "Assets", "HzbSettings.dataasset");
                File.WriteAllText(path, Serializer.Serialize(settings));
                Assert.True(database.RegisterAsset(new DataAssetAsset { Id = settings.Id, RelativePath = path }, path));
            }
            var output = Path.Combine(directory.FullName, "shot.png");
            SceneScreenshot.Capture(fixture.Vulkan, database, root, options, Bounds.Empty,
                output, logger, drawGizmos: true);
            Assert.True(File.Exists(output));
            var messages = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                .Select(call => call.GetArguments()[2]!.ToString()!);
            Assert.Contains(messages, message => message.Contains("Culling: 1 submitted, 1/2 submeshes culled",
                StringComparison.Ordinal));
            Assert.Equal(useOcclusion, messages.Any(message => message.Contains("GPU occlusion: 1 visible, 0/1 hidden",
                StringComparison.Ordinal)));
            Assert.Equal(2, root.Children.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
