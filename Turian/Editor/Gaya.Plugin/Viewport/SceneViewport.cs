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

    readonly Vulkan vulkan;
    readonly AssetDatabase assets;
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly GizmoDrawerCatalog gizmos;
    readonly PlayModeService playMode;
    readonly EditorCameraSettings cameraSettings;
    readonly LayerFilter layers;
    readonly ILogger log;
    readonly UndoService undo;
    readonly LocaleService locale;
    readonly SceneGridSettings gridSettings;
    readonly SceneOrientationWidget orientationWidget = new();

    readonly HashSet<int> heldKeys = [];

    SceneViewerService? service;
    SceneCameraController? controller;
    IUiPresenter? uiPresenter;
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
    bool showGizmoCursor;
    readonly ViewportGesture gesture = new();
    Vector2 pressPosition;
    Vector2 marqueePosition;
    bool marqueeOwnsDrag;
    bool additiveSelection;
    bool toggleSelection;

    /// <summary>Creates the viewport and follows the framing requests the scene tree raises.</summary>
    /// <param name="vulkan">Shared device the offscreen target is allocated from.</param>
    /// <param name="assets">The asset database materials and textures are read from.</param>
    /// <param name="sceneTree">Supplies the hierarchy to render and raises framing requests.</param>
    /// <param name="inspector">Receives picks and supplies the node the gizmo transforms.</param>
    /// <param name="gizmos">Resolves the per-component gizmo drawers.</param>
    /// <param name="playMode">Consulted so gizmos and picking stay out of a running session.</param>
    /// <param name="cameraSettings">How the free camera responds to input.</param>
    /// <param name="log">Where an unusable device is reported.</param>
    /// <param name="undo">Records a whole gizmo drag as one step.</param>
    /// <param name="locale">Resolves UI text for the open editor project.</param>
    /// <param name="layers">Resolves the current project's named rendering layers.</param>
    public SceneViewport(
        Vulkan vulkan,
        AssetDatabase assets,
        SceneTreeController sceneTree,
        NodeInspectorController inspector,
        GizmoDrawerCatalog gizmos,
        PlayModeService playMode,
        EditorCameraSettings cameraSettings,
        ILogger log,
        UndoService undo,
        LocaleService locale,
        LayerFilter? layers = null)
    {
        this.vulkan = vulkan;
        this.assets = assets;
        this.sceneTree = sceneTree;
        this.inspector = inspector;
        this.gizmos = gizmos;
        this.playMode = playMode;
        this.cameraSettings = cameraSettings;
        this.log = log;
        this.undo = undo;
        this.locale = locale;
        this.layers = layers ?? new LayerFilter();
        gridSettings = cameraSettings.Grid;
        Gizmo.Settings = cameraSettings.Gizmos;
        Gizmo.ViewSettings = cameraSettings.View;

        sceneTree.FrameNodeRequested += OnFrameNodeRequested;
        inspector.SelectionChanged += OnSelectionChanged;

        Gizmo.DragStarted += OnGizmoDragStarted;
        Gizmo.DragEnded += OnGizmoDragEnded;
        Gizmo.TransformEdited += NotifyGizmoMutation;
    }

    /// <summary>The interactive transform gizmo, so the panel's toolbar can drive its mode and snap.</summary>
    public TransformGizmo Gizmo { get; } = new();

    /// <summary>The live settings shared by the toolbar and Settings pages.</summary>
    public EditorCameraSettings Settings => cameraSettings;

    /// <summary>The rendering layer definitions supplied by the current project settings.</summary>
    public IReadOnlyList<LayerSlot> RenderLayers => layers.Settings.RenderLayers;

    /// <summary>Gets whether the Scene camera is using orthographic projection.</summary>
    public bool IsOrthographic => controller?.Camera.IsOrthographic ?? false;

    /// <summary>Changes projection while retaining the selected object's framing.</summary>
    public void ToggleProjection() => controller?.ToggleProjection(inspector.SelectedNode?.GlobalTransform.Position);

    /// <summary>Moves the camera once along a command palette direction.</summary>
    public void MoveCamera(int direction)
    {
        if (direction < 0 || direction >= SceneNavigationBindings.Directions.Length) return;
        controller?.ApplyKeyboardMovement([direction], 0.1f, 0, 1, 2, 3, 4, 5);
    }

    /// <summary>The submitted and culled submesh counts from the scene viewport's latest frame.</summary>
    public RenderCullingStats CullingStats => service?.CullingStats ?? default;

    Vector2 ViewportSize => new(service?.Width ?? 0, service?.Height ?? 0);

    /// <summary>
    /// Builds feedback nodes during layout and draws the scene during the render pass using the resolved rectangle.
    /// Input is processed only in the render pass.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    public void Render(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render)
        {
            DrawGestureFeedback(gui);
            orientationWidget.Render(gui, controller, inspector.SelectedNode, cameraSettings.Gizmos);
            return;
        }

        if (failure is not null)
        {
            gui.DrawText(failure, StudioTheme.Current.Text(12), StudioTheme.Current.Error, centerInRect: false);
            return;
        }

        var rect = gui.CurrentNode.Rect;
        var width = (uint)Math.Max(1f, rect.W);
        var height = (uint)Math.Max(1f, rect.H);
        if (!EnsureService(width, height)) return;

        orientationWidget.Render(gui, controller, inspector.SelectedNode, cameraSettings.Gizmos);
        HandleInput(gui, rect);
        RenderFrame(gui, rect);
        DrawGestureFeedback(gui);
    }

    void DrawGestureFeedback(Gui gui)
    {
        var text = Gizmo.IsDragging && Gizmo.HandleMode == TransformGizmoMode.Rotate
            ? $"{Gizmo.RotationDegrees:0.#}°" : string.Empty;
        using (gui.Node(120f, 24f, "scene/rotationFeedback").Absolute(12f, 12f).Enter())
            gui.DrawText(text, StudioTheme.Current.Text(13), StudioTheme.Current.Ink, centerInRect: false);
        var pointer = gui.Input.MousePosition;
        using (gui.Node(24f, 24f, "scene/gizmoCursor").AbsoluteScreen(pointer.X - 12f, pointer.Y - 12f)
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
            gui.DrawText(showGizmoCursor ? EditorIcons.Move : string.Empty,
                StudioTheme.Current.Text(18), StudioTheme.Current.Ink);
    }

    /// <summary>The installed interface package's presenter, created on first use; null when the project has none.</summary>
    IUiPresenter? UiPresenter() => uiPresenter ??= UiPresenters.Find()?.Create(vulkan, null, locale);

    /// <summary>Creates the renderer on the first frame and follows the node's size after that.</summary>
    bool EnsureService(uint width, uint height)
    {
        try
        {
            if (service is null)
            {
                service = new SceneViewerService(vulkan, assets, width, height);
                service.OnPopulateGizmos = PopulateGizmos;
                service.OverlaySource = (w, h, dt) =>
                    overlayRoot is null ? null : UiPresenter()?.TryRenderOverlay(overlayRoot, (int)w, (int)h, dt);
                service.WorldUiSource = frameInfo =>
                    overlayRoot is null ? [] : UiPresenter()?.RenderWorldPanels(overlayRoot, frameInfo) ?? [];
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

        var input = gui.Input;
        var interactable = gui.GetInteractable();
        var hovered = interactable.OnHover();

        SyncCamera(input);
        var phase = gesture.Update(button => interactable.OnHold(button switch
        {
            ViewportButton.Right => MouseButton.Right,
            ViewportButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left,
        }));
        controller.SetActiveButton(gesture.Active);

        var local = new Vector2(input.MousePosition.X - rect.X, input.MousePosition.Y - rect.Y);
        if (phase == ViewportGesturePhase.Pressed)
        {
            ReadSelectionModifiers(input);
        }
        DispatchPointer(phase, local, hovered);
        UpdateGizmoCursor(gui, hovered);

        if (hovered && input.MouseWheelDelta != 0f) controller.OnWheel(input.MouseWheelDelta);
        if (hovered || gesture.Active is not null) HandleKeyboard(gui);
        else heldKeys.Clear();
    }

    void ReadSelectionModifiers(IInputHandler input)
    {
        additiveSelection = input.IsKeyDown(KeyboardKey.LeftShift) || input.IsKeyDown(KeyboardKey.RightShift);
        toggleSelection = input.IsKeyDown(KeyboardKey.LeftControl) || input.IsKeyDown(KeyboardKey.RightControl)
            || input.IsKeyDown(KeyboardKey.LeftSuper) || input.IsKeyDown(KeyboardKey.RightSuper);
    }

    void UpdateGizmoCursor(Gui gui, bool hovered)
    {
        showGizmoCursor = hovered && !playMode.IsActive && Gizmo.Axis != TransformGizmoAxis.None;
        if (showGizmoCursor) gui.RequestPointerMode(PointerMode.Hidden);
        if (!hovered && !gizmoOwnsDrag) Gizmo.ClearHover();
    }

    // Pushed every frame rather than wired to a change event: the assignments cost nothing, and an edit made
    // while dragging the view takes effect without leaving the panel.
    void SyncCamera(IInputHandler input)
    {
        service!.ClearColor = new Vector4(cameraSettings.View.EmptySkyColor, 1f);
        service.Camera.CullingMask = cameraSettings.View.VisibleLayers;
        controller!.MoveSpeed = cameraSettings.MoveSpeed;
        controller.LookSensitivity = cameraSettings.LookSensitivity;
        controller.ZoomFraction = cameraSettings.ZoomFraction;
        controller.Camera.FieldOfView = cameraSettings.FieldOfView * MathF.PI / 180f;
        controller.Camera.NearPlane = cameraSettings.NearClip;
        controller.Camera.FarPlane = Math.Max(cameraSettings.FarClip, controller.Camera.NearPlane + 0.01f);
        controller.IsAlt = input.IsKeyDown(KeyboardKey.LeftAlt) || input.IsKeyDown(KeyboardKey.RightAlt);
        controller.IsFast = input.IsKeyDown(KeyboardKey.LeftShift) || input.IsKeyDown(KeyboardKey.RightShift);
        Gizmo.SnapEnabled = cameraSettings.Tools.SnapEnabled;
        Gizmo.SnapTranslation = cameraSettings.Tools.TranslationSnap;
        Gizmo.SnapRotation = cameraSettings.Tools.RotationSnap;
        Gizmo.SnapScale = cameraSettings.Tools.ScaleSnap;
        Gizmo.CenterPivot = cameraSettings.Tools.CenterPivot;
    }

    void DispatchPointer(ViewportGesturePhase phase, Vector2 local, bool hovered)
    {
        if (phase == ViewportGesturePhase.Pressed) OnPressed(local);
        else if (phase == ViewportGesturePhase.Released) OnReleased(local, gesture.Released!.Value);
        else if (phase == ViewportGesturePhase.Dragged) OnDragged(local);
        else if (hovered && !playMode.IsActive) Gizmo.ProcessPointerMove(local, service!.Camera, ViewportSize);
    }

    /// <summary>
    /// A plain left press goes to the gizmo first; when it takes a handle the camera stays put for the
    /// rest of the gesture.
    /// </summary>
    void OnPressed(Vector2 local)
    {
        pressPosition = local;
        marqueePosition = local;
        gizmoOwnsDrag = false;
        marqueeOwnsDrag = false;

        if (controller!.IsLeftButton && !controller.IsAlt
            && !controller.IsMiddleButton && !controller.IsRightButton && !playMode.IsActive)
        {
            Gizmo.ProcessPointerDown(local, service!.Camera, ViewportSize);
            gizmoOwnsDrag = Gizmo.IsDragging;
            marqueeOwnsDrag = !gizmoOwnsDrag;
        }

        if (!gizmoOwnsDrag) controller.OnMouseDown(local.X, local.Y);
    }

    void OnDragged(Vector2 local)
    {
        if (gizmoOwnsDrag) Gizmo.ProcessPointerMove(local, service!.Camera, ViewportSize);
        else if (marqueeOwnsDrag) marqueePosition = local;
        else controller!.OnMouseMove(local.X, local.Y);
    }

    /// <summary>
    /// A left press that moved nowhere and drove neither the gizmo nor the camera is a pick. Hitting
    /// nothing clears the selection, which is what <c>Select(null)</c> already means.
    /// </summary>
    void OnReleased(Vector2 local, ViewportButton button)
    {
        if (gizmoOwnsDrag)
        {
            Gizmo.ProcessPointerUp();
            gizmoOwnsDrag = false;
            controller!.OnMouseUp();
            return;
        }

        if (!playMode.IsActive && sceneTree.CurrentSceneRoot is { } root)
            SelectReleased(root, local, button);
        marqueeOwnsDrag = false;
        controller!.OnMouseUp();
    }

    bool IsSelectionClick(Vector2 local, ViewportButton button) => button == ViewportButton.Left
        && !controller!.IsAlt
        && Math.Abs(local.X - pressPosition.X) <= clickDragThreshold
        && Math.Abs(local.Y - pressPosition.Y) <= clickDragThreshold;

    void SelectReleased(Node root, Vector2 local, ViewportButton button)
    {
        if (IsSelectionClick(local, button))
            inspector.SelectNode(ScenePicker.Pick(root, service!.Camera, local, ViewportSize, cameraSettings.View),
                additiveSelection, toggleSelection);
        else if (marqueeOwnsDrag && button == ViewportButton.Left)
        {
            var selected = SceneMarquee.Pick(root, service!.Camera, pressPosition, local,
                ViewportSize, cameraSettings.View);
            var previous = inspector.SelectedNodes;
            inspector.SelectMany(toggleSelection ? previous.Except(selected).Concat(selected.Except(previous))
                : additiveSelection ? previous.Concat(selected) : selected);
        }
    }

    void HandleKeyboard(Gui gui)
    {
        heldKeys.Clear();
        cameraSettings.Navigation?.Read(gui.Input, heldKeys, gui.Focus.IsTextInputFocused);
    }

    // ── Frame ───────────────────────────────────────────────────────────────

    void RenderFrame(Gui gui, Rect rect)
    {
        var dt = gui.Time.DeltaTime;

        controller!.ApplyKeyboardMovement(
            heldKeys, dt,
            moveForward: 0, moveBackward: 1, moveLeft: 2, moveRight: 3, moveUp: 4, moveDown: 5);

        overlayRoot = sceneTree.CurrentSceneRoot ?? service!.EditorOverlayRoot;
        service!.Render(overlayRoot, dt);
        service.CopyPixels(pixels);

        frame?.Dispose();
        frame = Snapshot(pixels, service.Width, service.Height);
        if (frame is not null) gui.DrawImage(frame, rect);
        DrawMarquee(gui, rect);

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

        GroundGrid.Draw(g, service.Camera.Position, gridSettings, cameraSettings.Gizmos);

        if (!cameraSettings.Gizmos.Visible) return;

        foreach (var selected in inspector.SelectedNodes)
            foreach (var component in selected.Components)
                foreach (var drawer in gizmos.GetDrawers(component.GetType()))
                    drawer.DrawGizmos(g, component);

        Gizmo.Draw(g, service.Camera, ViewportSize);
    }

    void OnGizmoDragStarted()
    {
        undo.BeginGesture();
        mutateNode = inspector.CreateMutationNotifier();
    }

    void OnGizmoDragEnded()
    {
        NotifyGizmoMutation();
        mutateNode = null;
        undo.EndGesture();
    }

    void NotifyGizmoMutation()
    {
        foreach (var node in inspector.SelectedNodes) mutateNode?.Invoke(node);
    }

    // ── Selection ───────────────────────────────────────────────────────────

    void OnSelectionChanged()
    {
        Gizmo.SelectedNode = inspector.SelectedNode;
        Gizmo.SelectedNodes = inspector.SelectedNodes;
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
            previewService ??= new SceneViewerService(vulkan, assets, width, height);
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

    void DrawMarquee(Gui gui, Rect viewport)
    {
        if (!marqueeOwnsDrag || Vector2.DistanceSquared(pressPosition, marqueePosition) <= 16f) return;
        var start = Vector2.Clamp(Vector2.Min(pressPosition, marqueePosition), Vector2.Zero, ViewportSize);
        var end = Vector2.Clamp(Vector2.Max(pressPosition, marqueePosition), Vector2.Zero, ViewportSize);
        var rect = new Rect(viewport.X + start.X, viewport.Y + start.Y, end.X - start.X, end.Y - start.Y);
        gui.DrawRect(rect, StudioTheme.Current.AccentFill);
        gui.DrawRectBorder(rect, StudioTheme.Current.Accent);
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
        uiPresenter?.Dispose();
        uiPresenter = null;
    }
}
