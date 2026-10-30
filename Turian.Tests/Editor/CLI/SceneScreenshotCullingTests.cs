using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Verifies headless screenshots report the submeshes actually submitted to their view.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneScreenshotCullingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Screenshot interface construction receives the selected project locale.</summary>
    [Fact]
    public void ScreenshotPassesLocaleOverrideToInterface()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var locale = new LocaleService();
        locale.SetLocale("pt-BR");
        using var services = new ServiceCollection().AddSingleton<ILocaleContext>(locale).BuildServiceProvider();
        var factory = Substitute.For<IUiPresenterFactory>();
        var presenter = Substitute.For<IUiPresenter>();
        factory.Create(fixture.Vulkan, null, services).Returns(presenter);
        var directory = Directory.CreateTempSubdirectory("turian-locale-shot-");
        try
        {
            var options = new ScreenshotOptions { Width = 32, Height = 32, Frames = 1, Yaw = 0, Pitch = 0, Headlight = 0 };
            SceneScreenshot.Capture(fixture.Vulkan, new AssetDatabase(), new Node(), options, Bounds.Empty,
                Path.Combine(directory.FullName, "shot.png"), NullLogger.Instance, services: services, factory: factory);
            factory.Received(1).Create(fixture.Vulkan, null, services);
            Assert.Equal("pt-BR", services.GetRequiredService<ILocaleContext>().ActiveLocale);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A screenshot logs its culling counts and writes a PNG while cleaning up its temporary headlight.</summary>
    [Fact]
    public void ScreenshotReportsItsCullingCounts()
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
            var output = Path.Combine(directory.FullName, "shot.png");
            SceneScreenshot.Capture(fixture.Vulkan, database, root, options, Bounds.Empty,
                output, logger, drawGizmos: true);
            Assert.True(File.Exists(output));
            var messages = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                .Select(call => call.GetArguments()[2]!.ToString()!).ToList();
            Assert.Contains(messages, message => message.Contains("Culling: 1 submitted, 1/2 submeshes culled",
                StringComparison.Ordinal));
            Assert.DoesNotContain(messages, message => message.Contains("GPU occlusion", StringComparison.Ordinal));
            Assert.Equal(2, root.Children.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
