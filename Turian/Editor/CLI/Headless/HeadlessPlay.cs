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
    /// <param name="project">The opened project, which owns the render device when graphics were requested.</param>
    /// <param name="root">Root of the scene to play.</param>
    /// <param name="frames">How many frames to tick.</param>
    /// <param name="outputPath">PNG to write, or <c>null</c> to run without rendering.</param>
    /// <param name="options">Viewport size and fallback camera framing.</param>
    /// <param name="bounds">World bounds, used when the scene has no camera to render through.</param>
    /// <param name="locale">Locale to force for the session, or <c>null</c> to use the project's default.</param>
    /// <param name="logger">The logger progress is reported through.</param>
    /// <param name="statisticsPath">Optional JSON report that enables offscreen rendering.</param>
    /// <param name="realtime">
    /// Advance each frame by the wall-clock time it took instead of exactly one fixed step; see <see cref="TickFrame"/>.
    /// </param>
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
        string? statisticsPath = null,
        bool realtime = false)
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

        ISceneCapture? renderer = null;
        try
        {
            renderer = CreateRenderer(project, play, options, outputPath is not null || statisticsPath is not null);
            var stats = RunFrames(play, renderer, frames, realtime, logger);
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

    static ISceneCapture? CreateRenderer(HeadlessProject project, PlayModeService play, ScreenshotOptions options,
        bool requested)
    {
        if (!requested || project.Renderer is null || project.Device is null)
        {
            return null;
        }

        return project.Renderer.CreateCapture(project.Device, project.Database,
            new SceneCaptureOptions(options.Width, options.Height)
            {
                CollectStatistics = true,
                Services = play.Services,
                Input = play.Input,
                Playing = true,
            });
    }

    /// <summary>The presentation delta a realtime frame is rendered with, as before fixed stepping became the default.</summary>
    const double RealtimePresentationDelta = 1.0 / 60.0;

    /// <summary>
    /// Advances the session by one frame. By default the frame is exactly one fixed step, so <c>--frames N</c> runs
    /// N ticks on any machine and the result is reproducible. With <paramref name="realtime"/>, the frame advances by
    /// the wall-clock time since the previous one, as the Studio does, so a slow machine catches up with several ticks
    /// per frame.
    /// </summary>
    /// <param name="play">The running session.</param>
    /// <param name="realtime">Whether to advance by wall-clock time.</param>
    /// <returns>The delta to render the frame with, in seconds.</returns>
    internal static double TickFrame(PlayModeService play, bool realtime)
    {
        if (realtime)
        {
            play.Tick();
            return RealtimePresentationDelta;
        }

        var deltaTime = play.FixedDeltaTime;
        play.Tick(deltaTime);
        return deltaTime;
    }

    static RenderStats RunFrames(PlayModeService play, ISceneCapture? renderer, int frames, bool realtime,
        ILogger logger)
    {
        var stats = new RenderStats();
        for (var frame = 0; frame < frames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var deltaTime = TickFrame(play, realtime);
            if (renderer is not null && play.PlayRoot is { } playRoot)
            {
                renderer.Render(playRoot, deltaTime, play.ActiveCamera);
                RenderStatisticsTargets.Record(RenderStatisticsTargets.Find(playRoot), renderer.FrameStats);
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stats.AddFrame(elapsed, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, renderer?.FrameStats);
            if (frame == 0 || frame == frames - 1)
            {
                logger.LogInformation("Play frame {Frame}/{Frames}: {Elapsed:F1} ms", frame + 1, frames, elapsed);
            }
        }
        return stats;
    }

    static void SaveOutputs(HeadlessProject project, Node root, ISceneCapture? renderer, RenderStats stats,
        string? outputPath, string? statisticsPath, ILogger logger)
    {
        if (statisticsPath is not null)
        {
            stats.WriteJson(statisticsPath, project.SceneManager.GetLoadMilliseconds(root),
                project.Database.LoadStatistics.Snapshot, project.Renderer?.UploadedTextureBytes ?? 0);
            logger.LogInformation("Wrote frame statistics: {StatisticsPath}", statisticsPath);
        }
        if (renderer is null || outputPath is null)
        {
            return;
        }

        PngEncoder.Save(outputPath, renderer.ReadPixels(), alpha: false);
        logger.LogInformation("Wrote {Width}×{Height} play frame: {OutputPath}",
            renderer.Width, renderer.Height, outputPath);
    }

    /// <summary>
    /// The editor-side services a session reads: the project settings, so the session can resolve
    /// what they name, plus the render device when the project was opened with graphics.
    /// </summary>
    static IServiceProvider BuildEditorServices(HeadlessProject project)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAppSettings>(project.Settings);

        if (project.Device is not null)
        {
            _ = services.AddSingleton(project.Device);
        }

        return services.BuildServiceProvider();
    }
}
