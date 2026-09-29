namespace Turian.Tests;

/// <summary>Checks Output's play-boundary clearing without starting a window or Vulkan device.</summary>
public sealed class OutputLogBridgeTests
{
    [TypeId("be91f438-ddba-43ab-95e3-ad135a19477c")]
    sealed class FirstFrameLogger : Component
    {
        public override void OnUpdate(float deltaTime) =>
            LogBuffer.Provider.CreateLogger("Game").LogInformation("first game update");
    }

    /// <summary>Clearing on Play must finish before any game callback logs its first update.</summary>
    [Fact]
    public void ClearOnPlay_LeavesFirstGameUpdateVisible()
    {
        TestAssetDatabase.Reset();
        LogBuffer.Clear();
        try
        {
            using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
            using var editorServices = new ServiceCollection().BuildServiceProvider();
            var root = new Node();
            root.Components.Add(new FirstFrameLogger());
            var host = Substitute.For<IPlaySceneHost>();
            host.CurrentSceneRoot.Returns(root);
            var play = new PlayModeService(host, new AssetDatabase(), editorServices, NullLogger.Instance);
            var bridge = new OutputLogBridge(new OutputPanelSettings(), play, build, NullLogger.Instance);
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            logger.LogInformation("old editor message");

            Assert.True(play.Start());
            bridge.Tick();
            play.Tick(0.016);

            Assert.DoesNotContain(LogBuffer.Snapshot(), line => line.Text.Contains("old editor message"));
            Assert.Contains(LogBuffer.Snapshot(), line => line.Text.Contains("first game update"));
            play.Stop();
        }
        finally
        {
            LogBuffer.Clear();
            RuntimeServices.Reset();
            TestAssetDatabase.Reset();
        }
    }
}
