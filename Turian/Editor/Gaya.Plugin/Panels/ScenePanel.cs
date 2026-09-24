namespace Gaya.Plugin.Turian;

/// <summary>
/// The scene viewport: a transform-gizmo toolbar over the rendered scene. Falls back to naming the
/// active document when the open asset is not a scene.
/// </summary>
sealed class ScenePanel(SceneViewport viewport, SceneTreeController sceneTree,
    NodeInspectorController inspector, AssetWorkspace workspace)
    : IPanel, IDisposable
{
    static StudioTheme Theme => StudioTheme.Current;

    static float ToolbarHeight => Theme.Scale(26f);
    static float SnapWidth => Theme.Scale(64f);

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (sceneTree.CurrentSceneRoot is null)
        {
            DrawDocumentPlaceholder(gui);
            return;
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
        {
            Toolbar(gui);

            using (gui.Node().Expand().Enter())
                viewport.Render(gui);
        }
    }

    /// <summary>The viewport's transform gizmo, which the panel's W / E / R shortcuts switch modes on.</summary>
    public TransformGizmo Gizmo => viewport.Gizmo;

    /// <summary>Frames the selected node in the viewport. What the panel's F shortcut runs.</summary>
    public void FrameSelected()
    {
        if (sceneTree.CurrentSceneRoot is null) return;
        if (inspector.SelectedNode is not { } node) return;

        sceneTree.RequestFrameNode(node);
    }

    void DrawDocumentPlaceholder(Gui gui)
    {
        var document = workspace.Active;
        if (document?.Asset is null)
        {
            gui.DrawText("Open a scene to edit it here.", Theme.Text(12), Theme.InkDim, centerInRect: false);
            return;
        }

        gui.DrawText(document.DisplayTitle, Theme.Text(14), Theme.Ink, centerInRect: false);
        gui.DrawText(document.Asset.RelativePath, Theme.Text(11), Theme.InkDim, centerInRect: false);
        gui.DrawText(document.Asset is Prefab ? "Loading the scene…" : "Asset editor (S4)", Theme.Text(11), Theme.InkDim,
            centerInRect: false);
    }

    void Toolbar(Gui gui)
    {
        var gizmo = viewport.Gizmo;

        using (gui.Node(-1, ToolbarHeight, "scene/toolbar").ExpandWidth().Direction(Axis.Horizontal)
                   .Padding(4f, 3f).Gap(3f).Enter())
        {
            gui.DrawBackgroundRect(Theme.Chrome);

            ModeButton(gui, gizmo, "Move", TransformGizmoMode.Translate);
            ModeButton(gui, gizmo, "Rotate", TransformGizmoMode.Rotate);
            ModeButton(gui, gizmo, "Scale", TransformGizmoMode.Scale);

            using (gui.Node(Theme.Scale(8f), ToolbarHeight).Enter()) { } // spacer

            var isWorld = gizmo.Space == TransformGizmoSpace.World;
            if (Button(gui, isWorld ? "Global" : "Local", "scene/toolbar/space", selected: false))
                gizmo.Space = isWorld ? TransformGizmoSpace.Local : TransformGizmoSpace.World;

            using (gui.Node(Theme.Scale(8f), ToolbarHeight).Enter()) { } // spacer

            using (gui.Node(34f, ToolbarHeight, "scene/toolbar/snapLabel").ContentAlignY(0.5f).Enter())
                gui.DrawText("Snap", Theme.Text(11), Theme.InkDim, centerInRect: false);

            SnapField(gui, gizmo);
        }
    }

    /// <summary>The snap step of whichever mode is selected, matching how StudioA's single box behaves.</summary>
    static void SnapField(Gui gui, TransformGizmo gizmo)
    {
        var current = gizmo.Mode switch
        {
            TransformGizmoMode.Rotate => gizmo.SnapRotation,
            TransformGizmoMode.Scale => gizmo.SnapScale,
            _ => gizmo.SnapTranslation
        };

        var text = current.ToString("0.###", CultureInfo.InvariantCulture);
        var edited = gui.TextInput(text, width: SnapWidth, height: Theme.Scale(20f),
            fontSize: Theme.Text(11),
            backgroundColor: Theme.Field, borderColor: Theme.Border, textColor: Theme.Ink, padding: 4,
            id: "scene/toolbar/snap", alignX: 1f);

        if (string.Equals(edited, text, StringComparison.Ordinal)) return;
        if (!float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;

        switch (gizmo.Mode)
        {
            case TransformGizmoMode.Rotate: gizmo.SnapRotation = value; break;
            case TransformGizmoMode.Scale: gizmo.SnapScale = value; break;
            default: gizmo.SnapTranslation = value; break;
        }
    }

    static void ModeButton(Gui gui, TransformGizmo gizmo, string label, TransformGizmoMode mode)
    {
        if (Button(gui, label, $"scene/toolbar/{mode}", gizmo.Mode == mode)) gizmo.Mode = mode;
    }

    static bool Button(Gui gui, string label, string id, bool selected)
    {
        using (gui.Node(Theme.Scale((label.Length * 7f) + 14f), Theme.Scale(20f), id)
                   .ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (selected) gui.DrawBackgroundRect(Theme.AccentFill, 3f);
            else if (hot) gui.DrawBackgroundRect(Theme.Hover, 3f);

            gui.DrawText(label, Theme.Text(11), selected ? Theme.Ink : Theme.InkDim);
            return hot && interactable.OnClick();
        }
    }

    /// <inheritdoc />
    public void Dispose() => viewport.Dispose();
}
