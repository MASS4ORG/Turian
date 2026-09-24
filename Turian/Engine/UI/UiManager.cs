namespace Turian.Engine.UI;

/// <summary>
/// Finds the <see cref="UiDocumentComponent"/>s in a scene and composites the screen-space ones
/// into a single texture. A host calls <see cref="RenderOverlay"/> once per rendered frame with
/// the root being drawn, and assigns the result to the renderer's overlay slot.
/// </summary>
/// <remarks>
/// World-space panels are ignored here — they render to their own quads. Discovery is a tree
/// walk each frame, which is cheap for the handful of panels a scene carries and avoids any
/// registration-order coupling with the component lifecycle.
/// </remarks>
public sealed class UiManager : IDisposable
{
    readonly Vulkan vulkan;
    readonly List<UiDocumentComponent> found = [];
    readonly Dictionary<Guid, CachedRenderer> renderers = [];
    readonly Dictionary<Guid, WorldPanel> worldPanels = [];
    UiRuntime? overlayRuntime;
    bool disposed;

    sealed record WorldPanel(UiRuntime Runtime, WorldPanelInputHandler Input);

    /// <summary>Creates the manager on the shared Vulkan context.</summary>
    /// <param name="vulkan">The context UI textures are created on.</param>
    public UiManager(Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;
    }

    /// <summary>
    /// Renders every enabled screen-space <see cref="UiDocumentComponent"/> found under
    /// <paramref name="root"/>, in <see cref="UiDocumentComponent.SortOrder"/> order, into one
    /// shared surface.
    /// </summary>
    /// <param name="root">The scene root being rendered this frame.</param>
    /// <param name="framebufferWidth">Target width in physical pixels, greater than zero.</param>
    /// <param name="framebufferHeight">Target height in physical pixels, greater than zero.</param>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    /// <returns>The composited texture, or <c>null</c> when there is nothing to draw.</returns>
    public Texture? RenderOverlay(Node root, int framebufferWidth, int framebufferHeight, float deltaTime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        return RenderOverlayCore(root, framebufferWidth, framebufferHeight, deltaTime);
    }

    /// <summary>
    /// As <see cref="RenderOverlay"/>, but swallows and logs any exception and returns <c>null</c>,
    /// so a fault in a document, stylesheet or controller cannot blank the whole viewport frame.
    /// Editor viewports and the play loop call this.
    /// </summary>
    /// <param name="root">The scene root being rendered this frame.</param>
    /// <param name="framebufferWidth">Target width in physical pixels, greater than zero.</param>
    /// <param name="framebufferHeight">Target height in physical pixels, greater than zero.</param>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    public Texture? TryRenderOverlay(Node root, int framebufferWidth, int framebufferHeight, float deltaTime)
    {
        try
        {
            return RenderOverlay(root, framebufferWidth, framebufferHeight, deltaTime);
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "UI overlay render failed; skipping the overlay this frame");
            return null;
        }
    }

    Texture? RenderOverlayCore(Node root, int framebufferWidth, int framebufferHeight, float deltaTime)
    {
        found.Clear();
        Collect(root, found);

        var panels = found
            .Where(c => (c.OnBuild is not null || !c.Document.IsEmpty)
                        && c.IsActive
                        && c.IsAttached
                        && c.Node?.IsActive is true
                        && c.Mode is UiRenderMode.ScreenSpaceOverlay or UiRenderMode.ScreenSpaceCamera)
            .OrderBy(c => c.SortOrder)
            .ToArray();

        Diagnose($"panels: {found.Count} found, {panels.Length} eligible");
        if (panels.Length == 0) return null;

        var builders = new List<Action<Gui>>(panels.Length);
        foreach (var panel in panels)
        {
            if (panel.OnBuild is { } onBuild)
            {
                builders.Add(onBuild);
                continue;
            }

            var resolved = ResolveDocumentBuilder(panel);
            if (resolved is null) continue;

            resolved.Controller?.OnUpdate(deltaTime);
            builders.Add(resolved.Renderer.Render);
        }

        Diagnose($"builders: {builders.Count}");
        if (builders.Count == 0) return null;

        var first = panels[0];
        var scale = UiScale.Resolve(
            framebufferWidth, framebufferHeight,
            first.ScaleMode, first.ReferenceResolution.X, first.ReferenceResolution.Y);

        DiagnoseChange("size", $"fb {framebufferWidth}x{framebufferHeight}, mode {first.ScaleMode}, " +
                               $"ref {first.ReferenceResolution.X}x{first.ReferenceResolution.Y} -> " +
                               $"raster {scale.PixelWidth}x{scale.PixelHeight} canvasScale {scale.CanvasScale:0.###}");

        overlayRuntime ??= new UiRuntime(vulkan, scale.PixelWidth, scale.PixelHeight);

        var texture = overlayRuntime.Render(
            gui =>
            {
                foreach (var build in builders) build(gui);
            },
            scale.PixelWidth, scale.PixelHeight, deltaTime, scale.CanvasScale);

        Diagnose($"rendered -> texture {texture.Width}x{texture.Height}");
        return texture;
    }

    /// <summary>
    /// Renders every enabled <see cref="UiRenderMode.WorldSpace"/> panel under <paramref name="root"/>
    /// to its own texture and returns each as a <see cref="WorldUiQuad"/>: a unit quad (1&#160;m wide,
    /// <c>PanelSize.Y / PanelSize.X</c> m tall) at the node's world transform. A host feeds the result
    /// to <c>SceneViewerService.WorldUiSource</c>.
    /// </summary>
    /// <param name="root">The scene root being rendered this frame.</param>
    /// <param name="frame">The render target (camera, size) the world panels are rasterised against.</param>
    public IReadOnlyList<WorldUiQuad> RenderWorldPanels(Node root, WorldUiFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(root);

        found.Clear();
        Collect(root, found);

        var viewProjection = frame.Camera.GetViewMatrix() * frame.Camera.GetProjectionMatrix();
        var pointer = Input.MousePosition;
        var viewport = new Vector2(frame.Width, frame.Height);

        var quads = new List<WorldUiQuad>();
        foreach (var panel in found)
        {
            if (panel.Mode != UiRenderMode.WorldSpace
                || !panel.IsActive || !panel.IsAttached || panel.Node?.IsActive is not true
                || (panel.OnBuild is null && panel.Document.IsEmpty))
            {
                continue;
            }

            var pw = Math.Max(1, panel.PanelSize.X);
            var ph = Math.Max(1, panel.PanelSize.Y);

            if (!worldPanels.TryGetValue(panel.Id, out var wp))
            {
                // Ownership passes to worldPanels, which Dispose releases. A `using` here disposed the
                // runtime while the dictionary kept it, so the next Render threw ObjectDisposedException.
                var handler = new WorldPanelInputHandler();
                // Ownership transfers to WorldPanel on successful construction; the catch below
                // disposes the runtime if that transfer cannot be completed.
#pragma warning disable CA2000
                var runtime = new UiRuntime(vulkan, pw, ph, input: handler);
#pragma warning restore CA2000
                try
                {
                    worldPanels[panel.Id] = wp = new WorldPanel(runtime, handler);
                }
                catch
                {
                    runtime.Dispose();
                    throw;
                }
            }

            var aspect = (float)ph / pw;
            var model = Matrix4x4.CreateScale(1f, aspect, 1f) * panel.Node.GlobalTransform.Matrix4X4();

            // Pointer raycast — only when a UiRaycasterComponent opts the panel in.
            if (panel.Node.GetComponent<UiRaycasterComponent>() is { } raycaster
                && WorldPanelPointer.TryHit(model, viewProjection, pointer, viewport,
                    new Vector2(pw, ph), out var hitPixels)
                && WithinRange(raycaster, frame.Camera, panel.Node))
            {
                wp.Input.SetPointer(hitPixels);
            }
            else
            {
                wp.Input.SetPointer(null);
            }

            var build = panel.OnBuild;
            if (build is null)
            {
                var resolved = ResolveDocumentBuilder(panel);
                if (resolved is null) continue;
                resolved.Controller?.OnUpdate(frame.DeltaTime);
                build = resolved.Renderer.Render;
            }

            var texture = wp.Runtime.Render(build, pw, ph, frame.DeltaTime);
            quads.Add(new WorldUiQuad(texture, model));
        }

        DiagnoseChange("world", $"{quads.Count} world panel(s)");
        return quads;
    }

    static bool WithinRange(UiRaycasterComponent raycaster, ICamera camera, Node panelNode)
    {
        if (raycaster.MaxDistance <= 0f) return true;
        var d = (panelNode.GlobalTransform.Position - camera.Position).Length();
        return d <= raycaster.MaxDistance;
    }

    // Logs each distinct state line once per manager lifetime, so a per-frame call site is silent
    // in steady state. Set TURIAN_UI_DIAG=0 to silence entirely.
    readonly HashSet<string> seenDiag = [];

    void Diagnose(string message)
    {
        if (!seenDiag.Add(message)) return;
        if (Environment.GetEnvironmentVariable("TURIAN_UI_DIAG") == "0") return;
        Log.Logger.LogInformation("UiManager: {Message}", message);
    }

    // Logs whenever the value for a key changes — used for per-frame quantities (viewport size,
    // scale) so the log shows the timeline of a resize/zoom without spamming every frame.
    readonly Dictionary<string, string> lastByKey = [];

    void DiagnoseChange(string key, string message)
    {
        if (lastByKey.TryGetValue(key, out var prev) && prev == message) return;
        lastByKey[key] = message;
        if (Environment.GetEnvironmentVariable("TURIAN_UI_DIAG") == "0") return;
        Log.Logger.LogInformation("UiManager[{Key}]: {Message}", key, message);
    }

    /// <summary>
    /// Resolves a panel's <c>.ui</c> document, stylesheets and code-behind controller into a
    /// cached <see cref="UiRenderer"/>, rebuilt only when the referenced assets change.
    /// </summary>
    CachedRenderer? ResolveDocumentBuilder(UiDocumentComponent panel)
    {
        if (!AssetDatabase.TryGetInstance(out var db) || db is null)
        {
            Diagnose("no AssetDatabase; document panels cannot resolve");
            return null;
        }

        var docAsset = panel.Document.Resolve(db);
        if (docAsset is null)
        {
            Diagnose($"document asset {panel.Document.AssetId} did not resolve (not imported, or wrong asset type)");
            return null;
        }

        var document = docAsset.GetContent();
        if (document is null)
        {
            Diagnose($"document asset {panel.Document.AssetId} ({docAsset.RelativePath}) has no readable content");
            return null;
        }

        var sheetIds = panel.StyleSheets.Where(r => !r.IsEmpty).Select(r => r.AssetId).ToArray();
        var key = string.Join(',', sheetIds) + '|' + string.Join(',', document.StyleSheets)
                  + '|' + (document.ControllerType ?? string.Empty);

        if (renderers.TryGetValue(panel.Id, out var cached)
            && cached.DocumentId == panel.Document.AssetId
            && cached.SheetKey == key)
        {
            return cached;
        }

        var renderer = new UiRenderer(document)
        {
            ImageResolver = new UiImageResolver(db).Resolve,
            FontResolver = new UiFontResolver(db).Resolve,
            TextResolver = Localization.Resolve,
        };

        // <Style src="…"> entries, resolved relative to the document's own project path.
        var baseDir = Path.GetDirectoryName(docAsset.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        foreach (var src in document.StyleSheets)
        {
            var sheet = ResolveSheetByPath(db, baseDir, src);
            if (sheet is not null) renderer.StyleSheets.Add(sheet);
        }

        // Explicit component stylesheets take priority (added last).
        foreach (var reference in panel.StyleSheets)
        {
            var sheet = reference.Resolve(db)?.GetContent();
            if (sheet is not null) renderer.StyleSheets.Add(sheet);
        }

        var controller = CreateController(document.ControllerType);
        if (controller is not null)
            renderer.Bind(controller: controller);

        var entry = new CachedRenderer(panel.Document.AssetId, key, renderer, controller);
        renderers[panel.Id] = entry;
        return entry;
    }

    /// <summary>Instantiates the named <see cref="UiController"/> from any loaded assembly, or <c>null</c>.</summary>
    static UiController? CreateController(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var type = ResolveType(typeName);
        if (type is null)
        {
            // In a standalone game the user assembly is a project reference and loads only when a
            // user type is first touched; nothing forces that here. Probe the app directory.
            foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            {
                try { Assembly.LoadFrom(dll); }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException) { }
            }
            type = ResolveType(typeName);
        }

        if (type is null || !typeof(UiController).IsAssignableFrom(type))
        {
            Log.Logger.LogWarning("UI controller type '{Type}' was not found or is not a UiController", typeName);
            return null;
        }

        try
        {
            return (UiController?)Activator.CreateInstance(type);
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
        {
            Log.Logger.LogError(ex, "Failed to construct UI controller '{Type}'", typeName);
            return null;
        }
    }

    static Type? ResolveType(string typeName) =>
        Type.GetType(typeName)
        ?? AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName))
            .FirstOrDefault(t => t is not null);

    static StyleSheet? ResolveSheetByPath(AssetDatabase db, string baseDir, string src)
    {
        if (string.IsNullOrWhiteSpace(src) || src.StartsWith('{')) return null;

        // <Style src> is accepted both relative to the .ui file and relative to the project root.
        var candidates = new[]
        {
            (baseDir.Length == 0 ? src : $"{baseDir}/{src}").Replace('\\', '/'),
            src.Replace('\\', '/'),
        };

        var snapshot = db.GetAssetsSnapshot();
        foreach (var relative in candidates)
        {
            var record = snapshot.FirstOrDefault(r =>
                string.Equals(r.SourceRelativePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase));
            if (record is null) continue;

            var asset = new UiStyleSheetAsset { Id = record.AssetId, RelativePath = record.SourceRelativePath };
            return asset.GetContent();
        }

        return null;
    }

    static void Collect(Node node, List<UiDocumentComponent> into)
    {
        foreach (var component in node.Components)
            if (component is UiDocumentComponent ui)
                into.Add(ui);

        foreach (var child in node.Children)
            Collect(child, into);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        overlayRuntime?.Dispose();
        foreach (var wp in worldPanels.Values) wp.Runtime.Dispose();
        worldPanels.Clear();
        renderers.Clear();
        found.Clear();
    }

    sealed record CachedRenderer(
        Guid DocumentId, string SheetKey, UiRenderer Renderer, UiController? Controller);
}
