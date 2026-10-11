namespace Turian.Tests;

/// <summary>Checks immediate background refresh requests and cleanup without opening a window.</summary>
[Collection(SerialTests.Name)]
public sealed class FrameRequestBridgeTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Background logs, tasks, scene requests and settings wake the GUI until the bridge is disposed.</summary>
    [Fact]
    public async Task ChangesWakeGuiAndDisposeDisconnectsCallbacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"turian-frame-requests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new EditorSettings(NullLogger.Instance, Path.Combine(directory, "settings.json"));
            using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance)
                .AddSingleton<IEditorSettings>(settings).BuildServiceProvider();
            var wakes = 0;
            var gui = new Gui { WakeHost = () => Interlocked.Increment(ref wakes) };
            using var bridge = new FrameRequestBridge(gui, services);
            Assert.Throws<ArgumentNullException>(() => new FrameRequestBridge(null!, services));
            Assert.Throws<ArgumentNullException>(() => new FrameRequestBridge(gui, null!));
            var logger = LogBuffer.Provider.CreateLogger("FrameRequests");
            await Task.Run(() => logger.LogInformation("Background message"), TestContext.Current.CancellationToken);
            Assert.True(wakes > 0);
            var count = wakes;
            LogBuffer.Clear();
            Assert.True(wakes > count);
            count = wakes;
            settings.NotifyChanged("performance");
            Assert.True(wakes > count);
            count = wakes;
            services.GetRequiredService<BackgroundTaskManager>().Submit(new BackgroundTaskSpec { Label = "Import" });
            Assert.True(wakes > count);
            count = wakes;
            services.GetRequiredService<SceneTreeController>().RequestFrameNode(new Node());
            Assert.True(wakes > count);
            count = wakes;
            var tree = services.GetRequiredService<SceneTreeController>();
            tree.ShowRuntimeScene(new Node());
            Assert.True(wakes > count);
            if (fixture.Available)
            {
                count = wakes;
                var play = services.GetRequiredService<PlayModeService>();
                Assert.True(play.Start());
                Assert.True(wakes > count);
                play.Stop();
            }
            bridge.Dispose();
            bridge.Dispose();
            count = wakes;
            logger.LogInformation("After disposal");
            settings.NotifyChanged("performance");
            services.GetRequiredService<SceneTreeController>().RequestFrameNode(new Node());
            Assert.Equal(count, wakes);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
