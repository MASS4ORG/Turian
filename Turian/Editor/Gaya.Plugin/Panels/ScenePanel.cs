namespace Gaya.Plugin.Turian;

/// <summary>
/// The scene viewport: a transform-gizmo toolbar over the rendered scene, under a breadcrumb back to the scene
/// while a prefab is open in prefab mode. Falls back to naming the active document when the open asset is not a
/// scene.
/// </summary>
sealed class ScenePanel(SceneViewport viewport, SceneTreeController sceneTree,
    NodeInspectorController inspector, AssetWorkspace workspace, PrefabStage prefabStage)
    : IPanel, IDisposable
{
    static ThemeTokens Theme => ThemeTokens.Current;

    static float ToolbarHeight => Theme.Scale(26f);
    readonly SceneToolbar toolbar = new(viewport, () =>
    {
        if (inspector.SelectedNode is { } selected) sceneTree.RequestFrameNode(selected);
    });

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
            if (prefabStage.Trail.Count > 0) Breadcrumb(gui);
            toolbar.Render(gui);

            using (gui.Node().Expand().Enter())
                viewport.Render(gui);
        }
    }

    /// <summary>The viewport's transform gizmo, which the panel's W / E / R shortcuts switch modes on.</summary>
    public TransformGizmo Gizmo => viewport.Gizmo;

    /// <summary>Moves the Scene camera once in the requested navigation direction.</summary>
    public void MoveCamera(int direction) => viewport.MoveCamera(direction);

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
            return;
        }

        gui.DrawText(document.DisplayTitle, Theme.Text(14), Theme.Ink, centerInRect: false);
        gui.DrawText(document.Asset.RelativePath, Theme.Text(11), Theme.InkDim, centerInRect: false);
        gui.DrawText(document.Asset is Prefab ? "Loading the scene…" : "Asset editor (S4)", Theme.Text(11), Theme.InkDim,
            centerInRect: false);
    }

    /// <summary>The documents prefab mode came from, each a way back, then the prefab being edited.</summary>
    void Breadcrumb(Gui gui)
    {
        var trail = prefabStage.Trail;
        var returnTo = -1;

        using (gui.Node(-1, ToolbarHeight, "scene/breadcrumb").ExpandWidth().Direction(Axis.Horizontal)
                   .Padding(6f, 3f).Gap(4f).ContentAlignY(0.5f).Enter())
        {
            gui.DrawBackgroundRect(Theme.Panel);

            for (var i = 0; i < trail.Count; i++)
            {
                if (Button(gui, trail[i].Title, $"scene/breadcrumb/{i}", selected: false)) returnTo = i;
                gui.DrawText(EditorIcons.CaretRight, Theme.Text(10), Theme.InkFaint);
            }

            gui.DrawText(EditorIcons.Cube, Theme.Text(11), Theme.Accent);
            gui.DrawText(workspace.Active?.Title ?? string.Empty, Theme.Text(11), Theme.Ink, centerInRect: false);
        }

        if (returnTo >= 0) prefabStage.Return(returnTo);
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
