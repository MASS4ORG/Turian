namespace Turian.Tests;

/// <summary>Checks Output's play-boundary clearing without starting a window or Vulkan device.</summary>
[Collection(SerialTests.Name)]
public sealed class OutputLogBridgeTests
{
    [TypeId("be91f438-ddba-43ab-95e3-ad135a19477c")]
    sealed class FirstFrameLogger : Component
    {
        public override void OnAwake() =>
            LogBuffer.Provider.CreateLogger("Game").LogInformation("game awake");

        public override void OnUpdate(float deltaTime) =>
            LogBuffer.Provider.CreateLogger("Game").LogInformation("first game update");
    }

    /// <summary>Clearing on Play must finish before any game callback logs its first update.</summary>
    [Fact]
    public void ClearOnPlay_LeavesFirstGameUpdateVisible()
    {
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
            var bridge = new OutputLogBridge(new OutputPanelSettings(), play, build);
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            logger.LogInformation("old editor message");

            Assert.True(play.Start());
            bridge.Tick();
            play.Tick(0.016);

            Assert.DoesNotContain(LogBuffer.Snapshot(), static line => line.Text.Contains("old editor message"));
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("game awake"));
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("first game update"));
            play.Pause();
            play.Resume();
            bridge.Tick();
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("first game update"));
            play.Stop();
        }
        finally
        {
            LogBuffer.Clear();
        }
    }

    /// <summary>Operation logs survive completion even when the UI did not tick while the task ran.</summary>
    [Theory]
    [InlineData("Compile & Load", false)]
    [InlineData("Compile & Load", true)]
    [InlineData("Play", false)]
    [InlineData("Play", true)]
    [InlineData("Export", false)]
    [InlineData("Export", true)]
    public async Task OperationLogsSurviveTheNextUiTick(string taskName, bool fails)
    {
        LogBuffer.Clear();
        try
        {
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            using var build = new BuildManager(new AppSettings(), logger);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(), services, logger);
            var bridge = new OutputLogBridge(new OutputPanelSettings(), play, build);
            logger.LogInformation("old editor message");

            var status = await build.TaskRunner.EnqueueAsync(new LoggingTask(taskName, logger, fails),
                TestContext.Current.CancellationToken);
            logger.LogWarning("startup diagnostic after the task");
            bridge.Tick();
            bridge.Tick();

            var lines = LogBuffer.Snapshot();
            Assert.DoesNotContain(lines, static line => line.Text.Contains("old editor message"));
            Assert.Contains(lines, static line => line.Text.Contains("operation diagnostic"));
            Assert.Contains(lines, static line => line.Text.Contains("startup diagnostic after the task"));
            Assert.Equal(fails ? BuildTaskState.Failed : BuildTaskState.Succeeded, status.State);
            Assert.Equal(1, bridge.Generation);
            Assert.DoesNotContain(lines, static line => line.Text.Contains("at Turian."));
            if (fails)
            {
                Assert.Contains(lines, static line => line.Level == LogLevel.Error
                    && line.Text.Contains("com.example.missing") && line.Text.Contains("then retry"));
                var visible = LogView.Project(lines, "", true, true, false, false, true, studio: false);
                Assert.Contains(visible, static row => row.Line.Level == LogLevel.Error
                    && row.Line.Text.Contains("com.example.missing"));
            }
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>Enabling a clear preference after an operation does not erase its existing diagnostics.</summary>
    [Fact]
    public async Task DisabledClearsAreNotAppliedRetroactively()
    {
        LogBuffer.Clear();
        try
        {
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            using var build = new BuildManager(new AppSettings(), logger);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(), services, logger);
            var settings = new OutputPanelSettings { ClearOnRecompile = false, ClearOnBuild = false };
            var bridge = new OutputLogBridge(settings, play, build);
            logger.LogInformation("old editor message");
            await build.TaskRunner.EnqueueAsync(new LoggingTask("Export", logger, false),
                TestContext.Current.CancellationToken);
            await build.TaskRunner.EnqueueAsync(new LoggingTask("Compile & Load", logger, false),
                TestContext.Current.CancellationToken);
            settings.ClearOnRecompile = true;
            settings.ClearOnBuild = true;
            bridge.Tick();

            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("old editor message"));
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("operation diagnostic"));
            Assert.Equal(0, bridge.Generation);
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>Unexpected engine errors retain exception detail while disabled log levels remain silent.</summary>
    [Fact]
    public void BufferPreservesUnexpectedErrorsAndIgnoresDisabledLevels()
    {
        LogBuffer.Clear();
        try
        {
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            logger.Log(LogLevel.None, default, "hidden", null,
                static (_, _) => throw new InvalidOperationException("Disabled entries must not be formatted"));
            Assert.Empty(LogBuffer.Snapshot());

            logger.LogError(new IOException("disk unavailable"), "Reading the project failed");
            var line = Assert.Single(LogBuffer.Snapshot());
            Assert.Contains("Reading the project failed", line.Text);
            Assert.Contains("System.IO.IOException: disk unavailable", line.Text);
            Assert.True(line.IsInternal);
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>Cancelling a build keeps the cancellation message and releases the UI lock.</summary>
    [Fact]
    public async Task CancelledBuildKeepsItsDiagnostics()
    {
        LogBuffer.Clear();
        try
        {
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            using var build = new BuildManager(new AppSettings(), logger);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(), services, logger);
            var bridge = new OutputLogBridge(new OutputPanelSettings(), play, build);
            var pending = build.TaskRunner.EnqueueAsync(new CancelledTask(logger), TestContext.Current.CancellationToken);

            build.TaskRunner.CancelCurrent();
            var status = await pending;
            bridge.Tick();

            Assert.Equal(BuildTaskState.Cancelled, status.State);
            Assert.False(build.TaskRunner.IsUiLocked);
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("operation diagnostic"));
            Assert.Contains(LogBuffer.Snapshot(), static line => line.Text.Contains("Cancelled: Export"));
        }
        finally { LogBuffer.Clear(); }
    }

    sealed class CancelledTask(ILogger logger) : IBuildTask
    {
        public string Name => "Export";
        public bool LocksUi => true;

        public async Task<string> RunAsync(CancellationToken ct)
        {
            logger.LogInformation("operation diagnostic");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "done";
        }
    }

    sealed class LoggingTask(string name, ILogger logger, bool fails) : IBuildTask
    {
        public string Name => name;
        public bool LocksUi => true;

        public Task<string> RunAsync(CancellationToken ct)
        {
            logger.LogInformation("operation diagnostic");
            return fails
                ? Task.FromException<string>(new PackageException("com.example.missing is unavailable. Set its file: source in Bricks/manifest.json."))
                : Task.FromResult("operation succeeded");
        }
    }
}
