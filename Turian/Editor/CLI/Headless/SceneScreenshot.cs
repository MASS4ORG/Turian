namespace Turian.Editor.CLI;

/// <summary>
/// Renders a loaded hierarchy through the editor's offscreen viewer and writes the result as a PNG.
/// </summary>
static class SceneScreenshot
{
    /// <summary>
    /// Renders <paramref name="root"/> and writes <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="vulkan">The headless Vulkan device.</param>
    /// <param name="root">The hierarchy to render.</param>
    /// <param name="options">Camera, size and frame-count options.</param>
    /// <param name="bounds">World bounds used when framing the whole scene.</param>
    /// <param name="outputPath">Absolute path of the PNG to write.</param>
    /// <param name="logger">The logger timings are reported through.</param>
    /// <param name="drawGizmos">
    /// When <c>true</c>, populates a representative set of gizmos (axis lines, a wire cube, a wire
    /// sphere and an overlay ring) so the gizmo renderer can be verified headlessly.
    /// </param>
    public static void Capture(
        Vulkan vulkan,
        Node root,
        ScreenshotOptions options,
        Bounds bounds,
        string outputPath,
        ILogger logger,
        bool drawGizmos = false)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        using var runner = new ScreenshotRunner(vulkan, root, options, bounds, logger, drawGizmos);
        runner.CaptureAndSave(outputPath, logger);
    }

    /// <summary>
    /// Owns the offscreen viewer and the UI objects it composites for one screenshot. The overlay and
    /// world-panel sources pull from the runner's UI objects, and the runner releases the viewer before
    /// those objects, so the sources never run against a disposed UI manager.
    /// </summary>
    sealed class ScreenshotRunner : IDisposable
    {
        readonly SceneViewerService viewer;
        readonly Node root;
        readonly UiManager? uiManager;
        readonly Node? lightNode;
        readonly int frames;

        public ScreenshotRunner(
            Vulkan vulkan,
            Node root,
            ScreenshotOptions options,
            Bounds bounds,
            ILogger logger,
            bool drawGizmos)
        {
            this.root = root;
            frames = options.Frames;
            viewer = new SceneViewerService(vulkan, options.Width, options.Height);
            if (drawGizmos) viewer.OnPopulateGizmos = DrawTestGizmos;

            uiManager = new UiManager(vulkan);
            viewer.OverlaySource = (w, h, dt) => uiManager.TryRenderOverlay(root, (int)w, (int)h, dt);
            viewer.WorldUiSource = frame => uiManager.RenderWorldPanels(root, frame);

            PlaceCamera(viewer.Camera, root, options, bounds, logger);
            lightNode = options.Headlight > 0f ? AddHeadlight(root, viewer.Camera, options.Headlight) : null;
        }

        public void CaptureAndSave(string outputPath, ILogger logger)
        {
            try
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    viewer.Render(root, 1.0 / 60.0);
                    logger.LogInformation(
                        "Frame {Frame}/{Frames}: {Elapsed:F1} ms",
                        frame + 1,
                        frames,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                var pixels = new byte[viewer.Width * viewer.Height * 4];
                viewer.CopyPixels(pixels);
                PngWriter.Save(outputPath, pixels, viewer.Width, viewer.Height);

                logger.LogInformation(
                    "Wrote {Width}×{Height} screenshot: {OutputPath}",
                    viewer.Width,
                    viewer.Height,
                    outputPath);
            }
            finally
            {
                viewer.OverlaySource = null;
                viewer.WorldUiSource = null;
                if (lightNode is not null)
                {
                    _ = root.Children.Remove(lightNode);
                }
            }
        }

        public void Dispose()
        {
            viewer.OverlaySource = null;
            viewer.WorldUiSource = null;
            viewer.Dispose();
            uiManager?.Dispose();
        }
    }

    static void DrawTestGizmos(Gizmos gizmos)
    {
        var origin = Vector3.Zero;

        // World pass: axis trio + wire cube + wire sphere (depth-tested against the scene).
        gizmos.Color = new Vector4(1f, 0.2f, 0.2f, 1f);
        gizmos.DrawRay(origin, new Vector3(2f, 0f, 0f));
        gizmos.Color = new Vector4(0.2f, 1f, 0.2f, 1f);
        gizmos.DrawRay(origin, new Vector3(0f, 2f, 0f));
        gizmos.Color = new Vector4(0.2f, 0.2f, 1f, 1f);
        gizmos.DrawRay(origin, new Vector3(0f, 0f, 2f));

        gizmos.Color = new Vector4(1f, 1f, 0.2f, 1f);
        gizmos.DrawWireCube(new Vector3(3f, 0f, 0f), new Vector3(2f));

        gizmos.Color = new Vector4(1f, 1f, 1f, 1f);
        gizmos.DrawWireSphere(new Vector3(-3f, 0f, 0f), 1.5f);

        // Overlay pass: always-on-top ring.
        gizmos.Color = new Vector4(0f, 1f, 1f, 1f);
        gizmos.DepthTest = false;
        gizmos.DrawCircle(new Vector3(0f, 2.5f, 0f), Vector3.UnitY, 1.5f);
    }

    static void PlaceCamera(
        EditorCamera camera,
        Node root,
        ScreenshotOptions options,
        Bounds bounds,
        ILogger logger)
    {
        camera.Yaw = options.Yaw * (MathF.PI / 180f);
        camera.Pitch = options.Pitch * (MathF.PI / 180f);

        if (!string.IsNullOrWhiteSpace(options.CameraName))
        {
            if (TryPlaceAtSceneCamera(camera, root, options.CameraName, logger))
            {
                LogCamera(camera, logger);
                return;
            }

            logger.LogWarning(
                "No node named '{CameraName}' with a CameraComponent; falling back to the framing options",
                options.CameraName);
        }

        if (options.Position is { } position)
        {
            camera.Position = position;
        }
        else if (!bounds.IsEmpty)
        {
            var radius = bounds.Size.Length() * 0.5f;
            var distance = radius / MathF.Tan(camera.FieldOfView * 0.5f);
            camera.Position = bounds.Center - (camera.Front * distance);
            camera.FarPlane = MathF.Max(camera.FarPlane, (distance + (radius * 2f)) * 1.5f);
        }

        LogCamera(camera, logger);
    }

    /// <summary>
    /// Copies a named scene camera's placement onto the offscreen camera. Cameras authored in the
    /// scene are how a benchmark keeps framing the same shot across runs.
    /// </summary>
    static bool TryPlaceAtSceneCamera(EditorCamera camera, Node root, string name, ILogger logger)
    {
        var node = FindCameraNode(root, name);
        if (node is null)
        {
            return false;
        }

        var component = node.GetComponent<CameraComponent>()!;
        camera.Position = node.GlobalTransform.Position;
        camera.Yaw = component.Yaw;
        camera.Pitch = component.Pitch;
        camera.FieldOfView = component.FieldOfView;
        camera.NearPlane = component.NearPlane;
        camera.FarPlane = component.FarPlane;

        logger.LogInformation("Rendering from scene camera '{CameraName}'", node.Name);
        return true;
    }

    static Node? FindCameraNode(Node node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)
            && node.GetComponent<CameraComponent>() is not null)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindCameraNode(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    static void LogCamera(EditorCamera camera, ILogger logger) =>
        logger.LogInformation(
            "Camera at ({X:F2}, {Y:F2}, {Z:F2}) yaw {Yaw:F1}° pitch {Pitch:F1}° far {Far:F1}",
            camera.Position.X,
            camera.Position.Y,
            camera.Position.Z,
            camera.Yaw * (180f / MathF.PI),
            camera.Pitch * (180f / MathF.PI),
            camera.FarPlane);

    static Node AddHeadlight(Node root, EditorCamera camera, float intensity)
    {
        var node = new Node { Name = "__Headlight" };
        node.Transform.Position = camera.Position;
        node.AddComponent(LightComponent.CreatePointLight(intensity, new Vector4(1f)));

        node.Parent = root;
        root.Children.Add(node);
        return node;
    }
}
