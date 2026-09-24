namespace Turian.Editor.CLI;

public static partial class Program
{
    static string SafeRelativePath(string virtualPath, Guid assetId)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return $"{assetId:N}.bin";
        }

        var normalized = virtualPath.Replace('\\', '/');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => s is not "." and not "..")
            .ToArray();

        return segments.Length == 0 ? $"{assetId:N}.bin" : Path.Combine(segments);
    }

    /// <summary>
    /// Runs the editor's own play session headlessly, so play mode can be exercised — and a crash
    /// in it can produce a stack trace — without opening the Studio.
    /// </summary>
    static Command PlayModeCommand(Argument<FileSystemInfo> projectArg)
    {
        var sceneOption = SceneOption();
        var reimportOption = ReimportOption();
        var loadUserCodeOption = LoadUserCodeOption();
        var framesOption = new Option<int>("--frames") { Description = "Frames to tick", DefaultValueFactory = _ => 60 };
        var outputOption = new Option<string?>("--out") { Description = "PNG of the last frame; omit to run without rendering" };
        var widthOption = new Option<uint>("--width") { Description = "Output width in pixels", DefaultValueFactory = _ => 1280 };
        var heightOption = new Option<uint>("--height") { Description = "Output height in pixels", DefaultValueFactory = _ => 720 };
        var localeOption = LocaleOption();

        var cmd = new Command("playmode", "Run a play session headlessly, as the Studio's Play button does")
        {
            projectArg, sceneOption, reimportOption, loadUserCodeOption, framesOption, outputOption, widthOption, heightOption,
            localeOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            await PrepareProjectAsync(settings, result.GetValue(reimportOption), result.GetValue(loadUserCodeOption)).ConfigureAwait(false);

            var output = result.GetValue(outputOption);
            using var project = HeadlessProject.Open(settings, Log.Logger, withGraphics: output is not null);
            var root = project.LoadScene(result.GetValue(sceneOption));

            var report = SceneReport.Collect(root, loadModels: true);
            report.Write(Log.Logger);

            var options = new ScreenshotOptions
            {
                Width = result.GetValue(widthOption),
                Height = result.GetValue(heightOption),
                Frames = result.GetValue(framesOption),
                Yaw = 0f,
                Pitch = 0f,
                Headlight = 0f,
            };

            var ok = HeadlessPlay.Run(
                project,
                root,
                result.GetValue(framesOption),
                output is null ? null : Path.GetFullPath(output),
                options,
                report.Bounds,
                result.GetValue(localeOption),
                Log.Logger);

            return ok ? 0 : 1;
        });

        return cmd;
    }

    static Command PlayCommand(Argument<FileSystemInfo> projectArg)
    {
        var cmd = new Command("play", "Build and launch the project in play mode") { projectArg };
        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            Log.Logger.LogInformation("Playing {Title}…", settings.Title);

            await PrepareProjectAsync(settings, reimport: true, loadUserCode: false).ConfigureAwait(false);

            var player = new PlayUserCode(settings, Log.Logger);
            await player.ExecuteAsync();
        });
        return cmd;
    }

    static Command SceneCommand(Argument<FileSystemInfo> projectArg)
    {
        var sceneOption = SceneOption();
        var reimportOption = ReimportOption();
        var loadUserCodeOption = LoadUserCodeOption();

        var cmd = new Command("scene", "Load a scene headlessly and report what it contains")
        {
            projectArg, sceneOption, reimportOption, loadUserCodeOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            await PrepareProjectAsync(settings, result.GetValue(reimportOption), result.GetValue(loadUserCodeOption)).ConfigureAwait(false);

            using var project = HeadlessProject.Open(settings, Log.Logger, withGraphics: false);
            var root = project.LoadScene(result.GetValue(sceneOption));
            SceneReport.Collect(root).Write(Log.Logger);
        });

        return cmd;
    }

    /// <summary>
    /// Diagnostic for viewport picking: loads a scene with a live Vulkan device, then reports
    /// (a) how many <see cref="ModelComponent"/>s resolve non-empty world bounds and (b) what
    /// <see cref="ScenePicker.Pick"/> hits at a given screen point through a named scene camera.
    /// Exists to test the ray/bounds pipeline against real assets without needing the Studio
    /// window — see the "picking selects nothing" investigation in AGENTS.md-adjacent notes.
    /// </summary>
    static Command PickCommand(Argument<FileSystemInfo> projectArg)
    {
        var sceneOption = SceneOption();
        var reimportOption = ReimportOption();
        var loadUserCodeOption = LoadUserCodeOption();
        var cameraOption = new Option<string>("--camera")
        {
            Description = "Name of the scene camera node to pick through",
            DefaultValueFactory = _ => "Main Camera",
        };
        var widthOption = new Option<uint>("--width") { Description = "Viewport width in pixels", DefaultValueFactory = _ => 1280 };
        var heightOption = new Option<uint>("--height") { Description = "Viewport height in pixels", DefaultValueFactory = _ => 720 };
        var xOption = new Option<float?>("--x") { Description = "Screen X to pick at; defaults to the viewport centre" };
        var yOption = new Option<float?>("--y") { Description = "Screen Y to pick at; defaults to the viewport centre" };

        var cmd = new Command("pick", "Diagnose viewport picking against a loaded scene, headlessly")
        {
            projectArg, sceneOption, reimportOption, loadUserCodeOption, cameraOption, widthOption, heightOption, xOption, yOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            await PrepareProjectAsync(settings, result.GetValue(reimportOption), result.GetValue(loadUserCodeOption)).ConfigureAwait(false);

            using var project = HeadlessProject.Open(settings, Log.Logger, withGraphics: true);
            var root = project.LoadScene(result.GetValue(sceneOption));

            var width = result.GetValue(widthOption);
            var height = result.GetValue(heightOption);
            var cameraName = result.GetValue(cameraOption)!;

            var cameraNode = FindNodeByName(root, cameraName);
            var camera = cameraNode?.GetComponent<CameraComponent>();
            if (camera is null)
            {
                Log.Logger.LogError("No node named '{CameraName}' with a CameraComponent", cameraName);
                return;
            }

            camera.Resize(width, height);

            var screenX = result.GetValue(xOption) ?? width / 2f;
            var screenY = result.GetValue(yOption) ?? height / 2f;

            ReportBoundsResolution(root, Log.Logger);

            var hit = ScenePicker.Pick(root, camera, new Vector2(screenX, screenY), new Vector2(width, height));
            Log.Logger.LogInformation(
                "Pick at ({X:F0}, {Y:F0}) through '{Camera}' ({Width}×{Height}): {Result}",
                screenX, screenY, cameraName, width, height,
                hit is null ? "no hit" : $"'{hit.Name}' (id {hit.Id})");
        });

        return cmd;
    }

    static void ReportBoundsResolution(Node root, ILogger logger)
    {
        var total = 0;
        var resolvedNonEmpty = 0;
        var resolvedEmpty = 0;
        var unresolved = 0;

        foreach (var component in Node.GetComponentsInChildren<ModelComponent>(root))
        {
            total++;
            var local = ModelBoundsUtility.ComputeLocalBounds(component, loadModels: true, out var isResolved);
            if (!isResolved) unresolved++;
            else if (local.IsEmpty) resolvedEmpty++;
            else resolvedNonEmpty++;
        }

        logger.LogInformation(
            "ModelComponent bounds: {Total} total, {NonEmpty} resolved with bounds, {Empty} resolved but empty, {Unresolved} unresolved",
            total, resolvedNonEmpty, resolvedEmpty, unresolved);
    }

    static Node? FindNodeByName(Node node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) return node;

        foreach (var child in node.Children)
        {
            if (FindNodeByName(child, name) is { } found) return found;
        }

        return null;
    }

    static Command ScreenshotCommand(Argument<FileSystemInfo> projectArg)
    {
        var sceneOption = SceneOption();
        var reimportOption = ReimportOption();
        var loadUserCodeOption = LoadUserCodeOption();
        var outputOption = new Option<string>("--out") { Description = "PNG file to write", DefaultValueFactory = _ => "screenshot.png" };
        var widthOption = new Option<uint>("--width") { Description = "Output width in pixels", DefaultValueFactory = _ => 1280 };
        var heightOption = new Option<uint>("--height") { Description = "Output height in pixels", DefaultValueFactory = _ => 720 };
        var framesOption = new Option<int>("--frames") { Description = "Frames to render before reading pixels back", DefaultValueFactory = _ => 2 };
        var positionOption = new Option<string?>("--position") { Description = "Camera position as 'x,y,z'; omit to frame the whole scene" };
        var yawOption = new Option<float>("--yaw") { Description = "Camera yaw in degrees" };
        var pitchOption = new Option<float>("--pitch") { Description = "Camera pitch in degrees" };
        var headlightOption = new Option<float>("--headlight")
        {
            Description = "Intensity of a point light attached to the camera; 0 for none. A scene "
                          + "viewed from d metres needs roughly d²"
        };
        var gizmosOption = new Option<bool>("--gizmos")
        {
            Description = "Draw a representative set of gizmos (axes, cube, sphere, overlay ring) "
                          + "to verify the gizmo renderer"
        };
        var cameraOption = new Option<string?>("--camera")
        {
            Description = "Render from the named scene camera node, ignoring --position/--yaw/--pitch"
        };
        var localeOption = LocaleOption();
        var cmd = new Command("screenshot", "Render a scene offscreen and write it as a PNG")
        {
            projectArg, sceneOption, reimportOption, loadUserCodeOption, outputOption, widthOption, heightOption,
            framesOption, positionOption, yawOption, pitchOption, headlightOption, gizmosOption,
            cameraOption, localeOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            await PrepareProjectAsync(settings, result.GetValue(reimportOption), result.GetValue(loadUserCodeOption)).ConfigureAwait(false);

            using var project = HeadlessProject.Open(settings, Log.Logger, withGraphics: true);
            var root = project.LoadScene(result.GetValue(sceneOption));

            if (result.GetValue(localeOption) is { } locale)
                Localization.SetLocale(locale);

            var report = SceneReport.Collect(root, loadModels: true);
            report.Write(Log.Logger);

            var options = new ScreenshotOptions
            {
                Width = result.GetValue(widthOption),
                Height = result.GetValue(heightOption),
                Frames = result.GetValue(framesOption),
                Yaw = result.GetValue(yawOption),
                Pitch = result.GetValue(pitchOption),
                Position = ScreenshotOptions.ParsePosition(result.GetValue(positionOption)),
                Headlight = result.GetValue(headlightOption),
                CameraName = result.GetValue(cameraOption),
            };

            SceneScreenshot.Capture(
                project.Vulkan!,
                root,
                options,
                report.Bounds,
                Path.GetFullPath(result.GetValue(outputOption)!),
                Log.Logger,
                drawGizmos: result.GetValue(gizmosOption));
        });

        return cmd;
    }

}
