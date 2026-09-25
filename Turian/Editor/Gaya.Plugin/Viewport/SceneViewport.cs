namespace Gaya.Plugin.Turian;

/// <summary>
/// Renders the open scene into a Guinevere layout node and routes the node's pointer and keyboard
/// input to the editor camera, the transform gizmo and scene picking. The Guinevere counterpart of
/// StudioA's <c>SceneViewerControl</c>: every navigation and gizmo decision still belongs to
/// <see cref="SceneCameraController"/> and <see cref="TransformGizmo"/> in <c>Editor.Core</c>.
/// </summary>
sealed class SceneViewport : IDisposable
{
    /// <summary>Pointer travel, in pixels, under which a press-release still counts as a click.</summary>
    const float clickDragThreshold = 4f;

    /// <summary>Longest edge of the selected camera's picture-in-picture preview.</summary>
    const int previewMaxWidth = 240;

    const float previewMargin = 12f;

    static readonly KeyboardKey[] movementKeys =
        [KeyboardKey.W, KeyboardKey.A, KeyboardKey.S, KeyboardKey.D, KeyboardKey.Q, KeyboardKey.E];

    readonly Vulkan vulkan;
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly GizmoDrawerCatalog gizmos;
    readonly PlayModeService playMode;
    readonly EditorCameraSettings cameraSettings;
    readonly ILogger log;

    readonly HashSet<int> heldKeys = [];

    SceneViewerService? service;
    SceneCameraController? controller;
    UiManager? uiManager;
    Node? overlayRoot;
    string? failure;

    byte[] pixels = [];
    SKImage? frame;

    SceneViewerService? previewService;
    CameraComponent? previewCamera;
    byte[] previewPixels = [];
    SKImage? previewFrame;

    Action<object>? mutateNode;
    bool gizmoOwnsDrag;
    MouseButton? activeButton;
    Vector2 pressPosition;

    /// <summary>Creates the viewport and follows the framing requests the scene tree raises.</summary>
    /// <param name="vulkan">Shared device the offscreen target is allocated from.</param>
    /// <param name="sceneTree">Supplies the hierarchy to render and raises framing requests.</param>
    /// <param name="inspector">Receives picks and supplies the node the gizmo transforms.</param>
    /// <param name="gizmos">Resolves the per-component gizmo drawers.</param>
    /// <param name="playMode">Consulted so gizmos and picking stay out of a running session.</param>
    /// <param name="cameraSettings">How the free camera responds to input.</param>
    /// <param name="log">Where an unusable device is reported.</param>
    public SceneViewport(
        Vulkan vulkan,
        SceneTreeController sceneTree,
        NodeInspectorController inspector,
        GizmoDrawerCatalog gizmos,
        PlayModeService playMode,
        EditorCameraSettings cameraSettings,
        ILogger log)
    {
        this.vulkan = vulkan;
        this.sceneTree = sceneTree;
        this.inspector = inspector;
        this.gizmos = gizmos;
        this.playMode = playMode;
        this.cameraSettings = cameraSettings;
        this.log = log;

        sceneTree.FrameNodeRequested += OnFrameNodeRequested;
        inspector.SelectionChanged += OnSelectionChanged;

        Gizmo.DragStarted += OnGizmoDragStarted;
        Gizmo.DragEnded += OnGizmoDragEnded;
        Gizmo.TransformEdited += NotifyGizmoMutation;
    }

    /// <summary>The interactive transform gizmo, so the panel's toolbar can drive its mode and snap.</summary>
    public TransformGizmo Gizmo { get; } = new();

    Vector2 ViewportSize => new(service?.Width ?? 0, service?.Height ?? 0);

    /// <summary>
    /// Draws one frame into the current layout node. Everything happens in the render pass: the
    /// node's rectangle — which sizes the offscreen target — is only resolved after layout.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    public void Render(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render) return;

        if (failure is not null)
        {
            gui.DrawText(failure, StudioTheme.Current.Text(12), StudioTheme.Current.Error, centerInRect: false);
            return;
        }

        var rect = gui.CurrentNode.Rect;
        var width = (uint)Math.Max(1f, rect.W);
        var height = (uint)Math.Max(1f, rect.H);
        if (!EnsureService(width, height)) return;

        HandleInput(gui, rect);
        RenderFrame(gui, rect);
    }

    /// <summary>Creates the renderer on the first frame and follows the node's size after that.</summary>
    bool EnsureService(uint width, uint height)
    {
        try
        {
            if (service is null)
            {
                service = new SceneViewerService(vulkan, width, height);
                service.OnPopulateGizmos = PopulateGizmos;
                uiManager = new UiManager(vulkan);
                service.OverlaySource = (w, h, dt) =>
                    overlayRoot is null ? null : uiManager.TryRenderOverlay(overlayRoot, (int)w, (int)h, dt);
                service.WorldUiSource = frameInfo =>
                    overlayRoot is null ? [] : uiManager.RenderWorldPanels(overlayRoot, frameInfo);
                controller = new SceneCameraController(service.Camera);
            }
            else if (service.Width != width || service.Height != height)
            {
                service.Resize(width, height);
            }
        }
        catch (Exception ex)
        {
            // A studio without a usable device still has to open its other panels.
            log.LogError(ex, "The scene viewport could not start");
            failure = "Scene rendering is unavailable — see the log.";
            return false;
        }

        if (pixels.Length != width * height * 4) pixels = new byte[width * height * 4];
        return true;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    void HandleInput(Gui gui, Rect rect)
    {
        if (controller is null || service is null) return;

        // Pushed every frame rather than wired to a change event: three assignments cost nothing, and
        // an edit made while dragging the view takes effect without leaving the panel.
        controller.MoveSpeed = cameraSettings.MoveSpeed;
        controller.LookSensitivity = cameraSettings.LookSensitivity;
        controller.ZoomFraction = cameraSettings.ZoomFraction;

        var input = gui.Input;
        var interactable = gui.GetInteractable();
        var hovered = interactable.OnHover();

        controller.IsAlt = input.IsKeyDown(KeyboardKey.LeftAlt) || input.IsKeyDown(KeyboardKey.RightAlt);
        controller.IsFast = input.IsKeyDown(KeyboardKey.LeftShift) || input.IsKeyDown(KeyboardKey.RightShift);

        // Only the button that owns the gesture is asked about. Guinevere keys its drag state by
        // element rather than by button, so asking about a button that is up would tear down the drag
        // another button is running — a right-drag look would stop on its second frame.
        var previous = activeButton;
        if (activeButton is { } held)
        {
            if (!interactable.OnHold(held)) activeButton = null;
        }
        else if (interactable.OnHold(MouseButton.Right)) activeButton = MouseButton.Right;
        else if (interactable.OnHold(MouseButton.Middle)) activeButton = MouseButton.Middle;
        else if (interactable.OnHold()) activeButton = MouseButton.Left;

        controller.IsLeftButton = activeButton == MouseButton.Left;
        controller.IsMiddleButton = activeButton == MouseButton.Middle;
        controller.IsRightButton = activeButton == MouseButton.Right;

        var local = new Vector2(input.MousePosition.X - rect.X, input.MousePosition.Y - rect.Y);

        if (activeButton is not null && previous is null) OnPressed(local);
        else if (activeButton is null && previous is { } released) OnReleased(local, released);
        else if (activeButton is not null) OnDragged(local);
        else if (hovered && !playMode.IsActive) Gizmo.ProcessPointerMove(local, service.Camera, ViewportSize);

        if (hovered && input.MouseWheelDelta != 0f) controller.OnWheel(input.MouseWheelDelta);
        if (hovered || activeButton is not null) HandleKeyboard(input);
        else heldKeys.Clear();
    }

    /// <summary>
    /// A plain left press goes to the gizmo first; when it takes a handle the camera stays put for the
    /// rest of the gesture.
    /// </summary>
    void OnPressed(Vector2 local)
    {
        pressPosition = local;
        gizmoOwnsDrag = false;

        if (controller!.IsLeftButton && !controller.IsAlt
            && !controller.IsMiddleButton && !controller.IsRightButton && !playMode.IsActive)
        {
            Gizmo.ProcessPointerDown(local, service!.Camera, ViewportSize);
            gizmoOwnsDrag = Gizmo.IsDragging;
        }

        if (!gizmoOwnsDrag) controller.OnMouseDown(local.X, local.Y);
    }

    void OnDragged(Vector2 local)
    {
        if (gizmoOwnsDrag) Gizmo.ProcessPointerMove(local, service!.Camera, ViewportSize);
        else controller!.OnMouseMove(local.X, local.Y);
    }

    /// <summary>
    /// A left press that moved nowhere and drove neither the gizmo nor the camera is a pick. Hitting
    /// nothing clears the selection, which is what <c>Select(null)</c> already means.
    /// </summary>
    void OnReleased(Vector2 local, MouseButton button)
    {
        if (gizmoOwnsDrag)
        {
            Gizmo.ProcessPointerUp();
            gizmoOwnsDrag = false;
            return;
        }

        var isClick = button == MouseButton.Left
            && !controller!.IsAlt
            && Math.Abs(local.X - pressPosition.X) <= clickDragThreshold
            && Math.Abs(local.Y - pressPosition.Y) <= clickDragThreshold;

        if (isClick && sceneTree.CurrentSceneRoot is { } root)
            inspector.Select(ScenePicker.Pick(root, service!.Camera, local, ViewportSize));

        controller!.OnMouseUp();
    }

    void HandleKeyboard(IInputHandler input)
    {
        heldKeys.Clear();
        foreach (var key in movementKeys)
            if (input.IsKeyDown(key))
                heldKeys.Add((int)key);

        if (input.IsKeyPressed(KeyboardKey.F) && inspector.SelectedNode is { } selected)
            controller!.FrameNode(selected);
    }

    // ── Frame ───────────────────────────────────────────────────────────────

    void RenderFrame(Gui gui, Rect rect)
    {
        var dt = gui.Time.DeltaTime;

        controller!.ApplyKeyboardMovement(
            heldKeys, dt,
            moveForward: (int)KeyboardKey.W,
            moveBackward: (int)KeyboardKey.S,
            moveLeft: (int)KeyboardKey.A,
            moveRight: (int)KeyboardKey.D,
            moveUp: (int)KeyboardKey.Q,
            moveDown: (int)KeyboardKey.E);

        overlayRoot = sceneTree.CurrentSceneRoot ?? service!.EditorOverlayRoot;
        service!.Render(overlayRoot, dt);
        service.CopyPixels(pixels);

        frame?.Dispose();
        frame = Snapshot(pixels, service.Width, service.Height);
        if (frame is not null) gui.DrawImage(frame, rect);

        RenderPreview(gui, rect, dt);
    }

    /// <summary>
    /// Draws the selected node's camera into the corner. Renders the
    /// edited hierarchy, never a play session's, and never resizes the camera it borrows.
    /// </summary>
    void RenderPreview(Gui gui, Rect rect, float dt)
    {
        if (previewService is null || previewCamera is null || sceneTree.CurrentSceneRoot is not { } root) return;

        previewService.Render(root, dt, previewCamera);
        previewService.CopyPixels(previewPixels);

        previewFrame?.Dispose();
        previewFrame = Snapshot(previewPixels, previewService.Width, previewService.Height);
        if (previewFrame is null) return;

        var target = new Rect(
            rect.X + rect.W - previewMargin - previewService.Width,
            rect.Y + rect.H - previewMargin - previewService.Height,
            previewService.Width,
            previewService.Height);

        gui.DrawImage(previewFrame, target);
        gui.DrawRectBorder(target, GuiColor.White);
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

    // ── Gizmos ──────────────────────────────────────────────────────────────

    void PopulateGizmos(Gizmos g)
    {
        if (playMode.IsActive || service is null) return;

        GroundGrid.Draw(g, service.Camera.Position);

        if (Gizmo.SelectedNode is { } selected)
            foreach (var component in selected.Components)
                foreach (var drawer in gizmos.GetDrawers(component.GetType()))
                    drawer.DrawGizmos(g, component);

        Gizmo.Draw(g, service.Camera, ViewportSize);
    }

    void OnGizmoDragStarted() => mutateNode = inspector.CreateMutationNotifier();

    void OnGizmoDragEnded()
    {
        NotifyGizmoMutation();
        mutateNode = null;
    }

    void NotifyGizmoMutation()
    {
        if (Gizmo.SelectedNode is { } node) mutateNode?.Invoke(node);
    }

    // ── Selection ───────────────────────────────────────────────────────────

    void OnSelectionChanged()
    {
        Gizmo.SelectedNode = inspector.SelectedNode;
        UpdatePreviewCamera(inspector.SelectedNode?.GetComponent<CameraComponent>());
    }

    void UpdatePreviewCamera(CameraComponent? camera)
    {
        if (camera is null || failure is not null)
        {
            DisposePreview();
            return;
        }

        previewCamera = camera;

        var aspect = camera.AspectRatio > 0.01f ? camera.AspectRatio : 16f / 9f;
        var width = (uint)previewMaxWidth;
        var height = (uint)Math.Max(1, MathF.Round(previewMaxWidth / aspect));

        try
        {
            previewService ??= new SceneViewerService(vulkan, width, height);
            if (previewService.Width != width || previewService.Height != height)
                previewService.Resize(width, height);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The camera preview could not start");
            DisposePreview();
            return;
        }

        if (previewPixels.Length != width * height * 4) previewPixels = new byte[width * height * 4];
    }

    void DisposePreview()
    {
        previewCamera = null;
        previewService?.Dispose();
        previewService = null;
        previewFrame?.Dispose();
        previewFrame = null;
        previewPixels = [];
    }

    void OnFrameNodeRequested(Node node, FrameNodeOptions options)
    {
        if (controller is null) return;

        if (options.MatchRotation && node.GetComponent<CameraComponent>() is { } camera)
        {
            controller.Camera.Yaw = camera.Yaw;
            controller.Camera.Pitch = camera.Pitch;
        }

        controller.FrameNode(node);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        sceneTree.FrameNodeRequested -= OnFrameNodeRequested;
        inspector.SelectionChanged -= OnSelectionChanged;
        Gizmo.DragStarted -= OnGizmoDragStarted;
        Gizmo.DragEnded -= OnGizmoDragEnded;
        Gizmo.TransformEdited -= NotifyGizmoMutation;

        DisposePreview();
        frame?.Dispose();
        frame = null;
        if (service is not null) service.OnPopulateGizmos = null;
        service?.Dispose();
        service = null;
        uiManager?.Dispose();
        uiManager = null;
    }
}
