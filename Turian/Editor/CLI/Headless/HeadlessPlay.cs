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
    /// <returns><c>true</c> when the session started, ticked and stopped without throwing.</returns>
    public static bool Run(
        HeadlessProject project,
        Node root,
        int frames,
        string? outputPath,
        ScreenshotOptions options,
        Bounds bounds,
        string? locale,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

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
            if (outputPath is not null && project.Vulkan is not null)
            {
                renderer = new PlayRenderer(project.Vulkan, options.Width, options.Height);
            }

            for (var frame = 0; frame < frames; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                play.Tick();

                if (renderer is not null && play.PlayRoot is { } playRoot)
                {
                    // Render through the session's own camera so this exercises the same path the
                    // Game panel takes, falling back to the offscreen camera when the scene has none.
                    renderer.Render(playRoot, 1.0 / 60.0, play.ActiveCamera);
                }

                if (frame == 0 || frame == frames - 1)
                {
                    logger.LogInformation(
                        "Play frame {Frame}/{Frames}: {Elapsed:F1} ms",
                        frame + 1,
                        frames,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            if (renderer is not null && outputPath is not null)
            {
                var pixels = new byte[renderer.Width * renderer.Height * 4];
                renderer.CopyPixels(pixels);
                PngWriter.Save(outputPath, pixels, renderer.Width, renderer.Height);
                logger.LogInformation("Wrote {Width}×{Height} play frame: {OutputPath}", renderer.Width, renderer.Height, outputPath);
            }

            return true;
        }
        finally
        {
            renderer?.Dispose();
            play.Stop();
            _ = bounds;
        }
    }

    /// <summary>
    /// Owns the offscreen viewer and its UI overlay for one headless play session. The overlay and
    /// world-panel sources pull from the session's current play root, which is refreshed before each
    /// render; the renderer disposes the viewer before the UI manager it feeds.
    /// </summary>
    sealed class PlayRenderer : IDisposable
    {
        readonly UiManager uiManager;
        readonly SceneViewerService viewer;
        Node? overlayRoot;

        public PlayRenderer(Vulkan vulkan, uint width, uint height)
        {
            uiManager = new UiManager(vulkan);
            viewer = new SceneViewerService(vulkan, width, height);
            viewer.OverlaySource = (w, h, dt) =>
                overlayRoot is null ? null : uiManager.TryRenderOverlay(overlayRoot, (int)w, (int)h, dt);
            viewer.WorldUiSource = frame =>
                overlayRoot is null ? Array.Empty<WorldUiQuad>() : uiManager.RenderWorldPanels(overlayRoot, frame);
        }

        public uint Width => viewer.Width;

        public uint Height => viewer.Height;

        public void Render(Node playRoot, double deltaTime, ICamera? activeCamera)
        {
            overlayRoot = playRoot;
            viewer.Render(playRoot, deltaTime, activeCamera);
        }

        public void CopyPixels(byte[] destination) => viewer.CopyPixels(destination);

        public void Dispose()
        {
            viewer.OverlaySource = null;
            viewer.WorldUiSource = null;
            viewer.Dispose();
            uiManager.Dispose();
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
