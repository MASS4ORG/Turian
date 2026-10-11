namespace Gaya.Plugin.Turian;

/// <summary>
/// Renders what the scene's primary camera sees, as a live, non-interactive preview of the
/// edited scene while nothing is playing, and the running session — fully interactive — once Play
/// starts. The Guinevere counterpart of StudioA's <c>GameViewControl</c>.
///
/// <para>
/// <see cref="ResolveSource"/> is the one place the two modes differ. Preview needs no session: it
/// reads the same live hierarchy the Scene View does, so moving or lighting objects there shows up
/// here on the next frame with no scripts running. Input is forwarded only while a session is
/// playing, so the preview is never interactive.
/// </para>
///
/// <para>
/// This viewport only <em>displays</em> a session; the session itself is pumped by
/// <see cref="GayaPlugin.Tick"/> at window level, so hiding the panel behind another dock tab
/// does not freeze the game.
/// </para>
/// </summary>
sealed class GameViewport : IDisposable
{
    readonly Vulkan vulkan;
    readonly AssetDatabase assets;
    readonly SceneTreeController sceneTree;
    readonly PlayModeService playMode;
    readonly ILogger log;
    readonly SceneViewSettings viewSettings;
    readonly FrameStatistics panelStatistics = new();

    bool showStatistics;
    bool statisticsVisible;
    GameStatisticsDrawing? statisticsDrawing;

    readonly HashSet<KeyboardKey> heldKeys = [];
    readonly HashSet<MouseButton> heldButtons = [];

    SceneViewerService? service;
    IUiPresenter? uiPresenter;
    Node? overlayRoot;
    IRenderStatisticsTarget? statisticsTarget;
    CameraComponent? lastResizedCamera;
    string? failure;

    byte[] pixels = [];
    SKImage? frame;

    /// <summary>Creates the viewport over the shared device and the editor's play session.</summary>
    /// <param name="vulkan">Shared device the offscreen target is allocated from.</param>
    /// <param name="assets">The asset database materials and textures are read from.</param>
    /// <param name="sceneTree">Supplies the edited hierarchy the stopped panel previews.</param>
    /// <param name="playMode">The session to display and forward input to.</param>
    /// <param name="log">Where an unusable device is reported.</param>
    /// <param name="cameraSettings">Shared environment preferences used by both Studio viewports.</param>
    public GameViewport(
        Vulkan vulkan, AssetDatabase assets, SceneTreeController sceneTree, PlayModeService playMode, ILogger log,
        EditorCameraSettings? cameraSettings = null)
    {
        this.vulkan = vulkan;
        this.assets = assets;
        this.sceneTree = sceneTree;
        this.playMode = playMode;
        this.log = log;
        viewSettings = cameraSettings?.View ?? new SceneViewSettings();

        playMode.StateChanged += OnPlayStateChanged;
    }

    /// <summary>Whether the Game panel displays its own statistics overlay during Play.</summary>
    public bool ShowStatistics
    {
        get => showStatistics;
        set
        {
            if (showStatistics == value) return;
            showStatistics = value;
            panelStatistics.Clear();
        }
    }

    /// <summary>Builds the panel overlay and draws the camera after layout resolves the viewport rectangle.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    public void Render(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build) statisticsVisible = ShowStatistics && playMode.State == PlayState.Playing;
        if (gui.Pass == Pass.Pass2Render) RenderViewport(gui);
        RenderStatisticsOverlay(gui);
    }

    void RenderStatisticsOverlay(Gui gui)
    {
        if (!statisticsVisible) return;
        using (gui.Node(-1, -1, "game/statistics").Expand().Absolute(0, 0).HitTestVisible(false).Enter())
        {
            gui.SetZIndex(1);
            if (gui.Pass != Pass.Pass2Render || !ShowStatistics || panelStatistics.Count == 0) return;
            statisticsDrawing ??= new GameStatisticsDrawing(panelStatistics, sceneTree);
            gui.CurrentNode.DrawList.Add(statisticsDrawing);
        }
    }

    void RenderViewport(Gui gui)
    {
        if (!PrepareFrame(gui)) return;
        var rect = gui.CurrentNode.Rect;
        var (root, camera) = ResolveSource();
        if (root is null || camera is null)
        {
            ReleaseInput();
            gui.DrawText("No camera in the scene.", ThemeTokens.Current.Text(12), ThemeTokens.Current.InkDim,
                centerInRect: false);
            return;
        }

        ResizeCamera(camera);
        ForwardInput(gui, rect);

        overlayRoot = root;
        RenderGameFrame(root, camera, gui.Time.DeltaTime);
        service!.CopyPixels(pixels);

        frame?.Dispose();
        frame = Snapshot(pixels, service.Width, service.Height);
        if (frame is not null) gui.DrawImage(frame, rect);
    }

    void RenderGameFrame(Node root, ICamera camera, double interval)
    {
        var target = playMode.State == PlayState.Playing ? RenderStatisticsTargets.Find(root) : null;
        statisticsTarget = target;
        var collectPanelStatistics = ShowStatistics && playMode.State == PlayState.Playing;
        service!.CollectStatistics = target is not null || collectPanelStatistics;
        service.Render(root, interval, camera);
        if (target is not null)
        {
            target.SceneLoadMilliseconds = sceneTree.CurrentSceneLoadMilliseconds;
        }
        RenderStatisticsTargets.Record(target, service.FrameStats);
        if (collectPanelStatistics) panelStatistics.Add(service.FrameStats);
    }

    bool PrepareFrame(Gui gui)
    {
        if (failure is not null)
        {
            gui.DrawText(failure, ThemeTokens.Current.Text(12), ThemeTokens.Current.Error, centerInRect: false);
            return false;
        }
        var rect = gui.CurrentNode.Rect;
        if (!EnsureService((uint)Math.Max(1f, rect.W), (uint)Math.Max(1f, rect.H))) return false;
        service!.ClearColor = new Vector4(viewSettings.EmptySkyColor, 1f);
        return true;
    }

    void ResizeCamera(ICamera camera)
    {
        // A primary-camera change needs a new aspect ratio even when the viewport size is unchanged.
        if (camera is not CameraComponent component || ReferenceEquals(component, lastResizedCamera)) return;
        component.Resize(service!.Width, service.Height);
        lastResizedCamera = component;
    }

    /// <summary>
    /// Resolves what this frame renders: the running session while one is active, otherwise the
    /// edited scene's own primary camera. The single switch between "preview" and "play".
    /// </summary>
    (Node? Root, ICamera? Camera) ResolveSource()
    {
        if (playMode.IsActive) return (playMode.PlayRoot, playMode.ActiveCamera);

        var root = sceneTree.EditorSceneRoot;
        return (root, root is null ? null : CameraComponent.FindPrimary(root));
    }

    /// <summary>The installed interface package's presenter, created on first use; null when the project has none.</summary>
    IUiPresenter? UiPresenter()
    {
        uiPresenter ??= UiPresenters.Find()?.Create(vulkan, playMode.Input, playMode.Locale);
        if (uiPresenter is not null) uiPresenter.IsPlaying = playMode.State == PlayState.Playing;
        return uiPresenter;
    }

    /// <summary>Creates the renderer on the first frame and follows the node's size after that.</summary>
    bool EnsureService(uint width, uint height)
    {
        try
        {
            if (service is null)
            {
                service = new SceneViewerService(vulkan, assets, width, height);
                service.OverlaySource = (w, h, dt) =>
                    overlayRoot is null ? null : UiPresenter()?.TryRenderOverlay(overlayRoot, (int)w, (int)h, dt);
                service.WorldUiSource = frameInfo =>
                    overlayRoot is null ? [] : UiPresenter()?.RenderWorldPanels(overlayRoot, frameInfo) ?? [];
            }
            else if (service.Width != width || service.Height != height)
            {
                service.Resize(width, height);
                lastResizedCamera = null;
            }
        }
        catch (Exception ex)
        {
            // A studio without a usable device still has to open its other panels.
            log.LogError(ex, "The game viewport could not start");
            failure = "Game rendering is unavailable — see the log.";
            return false;
        }

        if (uiPresenter is { } presenter) presenter.Locale = playMode.Locale;
        if (pixels.Length != width * height * 4) pixels = new byte[width * height * 4];
        return true;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the panel's pointer and keyboard into the session's input source while it is playing.
    /// Guinevere reports held state rather than press and release edges, and what it does call
    /// "up" differs between its backends, so the edges come from diffing against the previous frame.
    /// </summary>
    void ForwardInput(Gui gui, Rect rect)
    {
        if (playMode.State != PlayState.Playing)
        {
            ReleaseInput();
            return;
        }

        var input = gui.Input;
        gui.RegisterFocusable(canReceiveFocus: true, isInteractable: true);
        var interactable = gui.GetInteractable();
        var hovered = interactable.OnHover();

        if (hovered && interactable.OnClick()) gui.RequestFocus(FocusReason.Mouse);

        // Escape hands the keyboard back to the Studio, so a misbehaving session never traps it.
        if (gui.HasFocus() && input.IsKeyPressed(KeyboardKey.Escape))
        {
            gui.ClearFocus();
            ReleaseInput();
            return;
        }

        if (hovered)
        {
            playMode.Input.PushMouseMove(new Vector2(input.MousePosition.X - rect.X, input.MousePosition.Y - rect.Y));
            if (input.MouseWheelDelta != 0f) playMode.Input.PushMouseScroll(input.MouseWheelDelta);
        }

        DiffButtons(input, hovered);
        DiffKeys(input, gui.HasFocus());
    }

    void DiffButtons(IInputHandler input, bool active)
    {
        foreach (var button in GuinevereInputMap.Buttons)
        {
            var down = active && input.IsMouseButtonDown(button);
            if (down == heldButtons.Contains(button)) continue;
            if (GuinevereInputMap.ToSilkMouseButton(button) is not { } mapped) continue;

            if (down)
            {
                heldButtons.Add(button);
                playMode.Input.PushMouseDown(mapped);
            }
            else
            {
                heldButtons.Remove(button);
                playMode.Input.PushMouseUp(mapped);
            }
        }
    }

    void DiffKeys(IInputHandler input, bool active)
    {
        foreach (var key in GuinevereInputMap.Keys)
        {
            var down = active && input.IsKeyDown(key);
            if (down == heldKeys.Contains(key)) continue;
            if (GuinevereInputMap.ToSilkKey(key) is not { } mapped) continue;

            if (down)
            {
                heldKeys.Add(key);
                playMode.Input.PushKeyDown(mapped);
            }
            else
            {
                heldKeys.Remove(key);
                playMode.Input.PushKeyUp(mapped);
            }
        }
    }

    /// <summary>Otherwise a key held while clicking away, or while play stops, stays down forever.</summary>
    void ReleaseInput()
    {
        if (heldKeys.Count == 0 && heldButtons.Count == 0) return;

        heldKeys.Clear();
        heldButtons.Clear();
        playMode.Input.Clear();
    }

    /// <summary>Whichever camera just became the active one has to match the panel's current size.</summary>
    void OnPlayStateChanged(PlayState state)
    {
        _ = state;
        lastResizedCamera = null;
        statisticsTarget?.Statistics.Clear();
        panelStatistics.Clear();
        ReleaseInput();
    }

    /// <summary>
    /// Wraps the read-back pixels as an image for this frame. The copy is what makes the buffer safe
    /// to overwrite on the next one; the GPU-shared path is the follow-up to this.
    /// </summary>
    static SKImage? Snapshot(byte[] buffer, uint width, uint height)
    {
        if (width == 0 || height == 0) return null;

        var info = new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        playMode.StateChanged -= OnPlayStateChanged;

        frame?.Dispose();
        frame = null;
        service?.Dispose();
        service = null;
        uiPresenter?.Dispose();
        uiPresenter = null;
        overlayRoot = null;
        statisticsDrawing?.Dispose();
        statisticsDrawing = null;
    }
}
