namespace Turian.Editor.CLI;

/// <summary>
/// Runs a play session without the Studio: it drives the same <see cref="PlayModeService"/> the
/// Play button uses, ticks it for a set number of frames and optionally renders the result. This is
/// how play mode gets exercised — and how a crash in it gets a stack trace — without a window.
/// </summary>
static class HeadlessPlay
{
    /// <summary>
    /// Starts a session on <paramref name="root"/>, ticks it and writes a PNG of the last frame.
    /// </summary>
    /// <param name="project">The opened project, which owns the Vulkan device.</param>
    /// <param name="root">Root of the scene to play.</param>
    /// <param name="frames">How many frames to tick.</param>
    /// <param name="outputPath">PNG to write, or <c>null</c> to run without rendering.</param>
    /// <param name="options">Viewport size and fallback camera framing.</param>
    /// <param name="bounds">World bounds, used when the scene has no camera to render through.</param>
    /// <param name="locale">Locale to force for the session, or <c>null</c> to use the project's default.</param>
    /// <param name="logger">The logger progress is reported through.</param>
    /// <param name="statisticsPath">Optional JSON report that enables offscreen rendering.</param>
    /// <returns><c>true</c> when the session started, ticked and stopped without throwing.</returns>
    public static bool Run(
        HeadlessProject project,
        Node root,
        int frames,
        string? outputPath,
        ScreenshotOptions options,
        Bounds bounds,
        string? locale,
        ILogger logger,
        string? statisticsPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        var host = new HeadlessPlayHost(root, null);
        var editorServices = BuildEditorServices(project);
        var play = new PlayModeService(host, project.Database, editorServices, logger);

        if (!play.Start(locale))
        {
            logger.LogError("Play mode refused to start");
            return false;
        }

        PlayRenderer? renderer = null;
        try
        {
            renderer = CreateRenderer(project, play, options, outputPath is not null || statisticsPath is not null);
            var stats = RunFrames(play, renderer, frames, logger);
            stats.Report(logger);
            SaveOutputs(project, root, renderer, stats, outputPath, statisticsPath, logger);
            return true;
        }
        finally
        {
            renderer?.Dispose();
            play.Stop();
            _ = bounds;
        }
    }

    static PlayRenderer? CreateRenderer(HeadlessProject project, PlayModeService play, ScreenshotOptions options,
        bool requested)
    {
        if (!requested || project.Vulkan is null) return null;
        return new PlayRenderer(project.Vulkan, project.Database, play.Input, play.Locale,
            options.Width, options.Height);
    }

    static RenderStats RunFrames(PlayModeService play, PlayRenderer? renderer, int frames, ILogger logger)
    {
        var stats = new RenderStats();
        for (var frame = 0; frame < frames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            play.Tick();
            if (renderer is not null && play.PlayRoot is { } playRoot)
                renderer.Render(playRoot, 1.0 / 60.0, play.ActiveCamera);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stats.AddFrame(elapsed, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, renderer?.FrameStats);
            if (frame == 0 || frame == frames - 1)
                logger.LogInformation("Play frame {Frame}/{Frames}: {Elapsed:F1} ms", frame + 1, frames, elapsed);
        }
        return stats;
    }

    static void SaveOutputs(HeadlessProject project, Node root, PlayRenderer? renderer, RenderStats stats,
        string? outputPath, string? statisticsPath, ILogger logger)
    {
        if (statisticsPath is not null)
        {
            stats.WriteJson(statisticsPath, project.SceneManager.GetLoadMilliseconds(root),
                project.Database.LoadStatistics.Snapshot);
            logger.LogInformation("Wrote frame statistics: {StatisticsPath}", statisticsPath);
        }
        if (renderer is null || outputPath is null) return;
        var pixels = new byte[renderer.Width * renderer.Height * 4];
        renderer.CopyPixels(pixels);
        PngWriter.Save(outputPath, pixels, renderer.Width, renderer.Height);
        logger.LogInformation("Wrote {Width}×{Height} play frame: {OutputPath}",
            renderer.Width, renderer.Height, outputPath);
    }

    /// <summary>
    /// Owns the offscreen viewer and its UI overlay for one headless play session. The overlay and
    /// world-panel sources pull from the session's current play root, which is refreshed before each
    /// render; the renderer disposes the viewer before the UI manager it feeds.
    /// </summary>
    sealed class PlayRenderer : IDisposable
    {
        readonly IUiPresenter? uiPresenter;
        readonly SceneViewerService viewer;
        Node? overlayRoot;

        public PlayRenderer(
            Vulkan vulkan, AssetDatabase assets, IInputSource inputSource, LocaleService? locale, uint width, uint height)
        {
            uiPresenter = UiPresenters.Find()?.Create(vulkan, inputSource, locale);
            if (uiPresenter is not null) uiPresenter.IsPlaying = true;
            viewer = new SceneViewerService(vulkan, assets, width, height);
            viewer.CollectStatistics = true;
            if (uiPresenter is null) return;

            viewer.OverlaySource = (w, h, dt) =>
                overlayRoot is null ? null : uiPresenter.TryRenderOverlay(overlayRoot, (int)w, (int)h, dt);
            viewer.WorldUiSource = frame =>
                overlayRoot is null ? Array.Empty<WorldUiQuad>() : uiPresenter.RenderWorldPanels(overlayRoot, frame);
        }

        public uint Width => viewer.Width;

        public uint Height => viewer.Height;

        /// <summary>The viewer's completed render-call measurements.</summary>
        public RenderFrameStats FrameStats => viewer.FrameStats;

        public void Render(Node playRoot, double deltaTime, ICamera? activeCamera)
        {
            overlayRoot = playRoot;
            viewer.Render(playRoot, deltaTime, activeCamera);
            RenderStatisticsTargets.Record(RenderStatisticsTargets.Find(playRoot), viewer.FrameStats);
        }

        public void CopyPixels(byte[] destination) => viewer.CopyPixels(destination);

        public void Dispose()
        {
            viewer.OverlaySource = null;
            viewer.WorldUiSource = null;
            viewer.Dispose();
            uiPresenter?.Dispose();
        }
    }

    /// <summary>
    /// The editor-side services a session reads: the project settings, so the session can resolve
    /// what they name, plus the Vulkan device when the project was opened with graphics.
    /// </summary>
    static IServiceProvider BuildEditorServices(HeadlessProject project)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAppSettings>(project.Settings);

        if (project.Vulkan is not null)
        {
            _ = services.AddSingleton(project.Vulkan);
        }

        return services.BuildServiceProvider();
    }
}
