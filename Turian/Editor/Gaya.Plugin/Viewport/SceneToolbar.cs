namespace Gaya.Plugin.Turian;

/// <summary>Groups Scene tools into compact buttons, menus and editable transform and camera popovers.</summary>
sealed class SceneToolbar(SceneViewport viewport, Action frameSelected)
{
    static StudioTheme Theme => StudioTheme.Current;
    static float Height => Theme.Scale(28f);
    bool transformMenu;
    bool viewMenu;
    bool snapOptions;
    bool cameraOptions;
    Vector2 transformAt;
    Vector2 viewAt;
    Vector2 optionsAt;
    bool frameOptionsOpen;
    bool frameCameraOptions;
    Vector2 frameOptionsAt;
    Rect toolbarBounds;

    /// <summary>Builds the toolbar and processes its actions in the render pass.</summary>
    public void Render(Gui gui)
    {
        using (gui.Node(-1, Height, "scene/toolbar").ExpandWidth().Direction(Axis.Horizontal)
                   .Padding(4f, 3f).Gap(3f).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render) toolbarBounds = gui.CurrentNode.Rect;
            gui.DrawBackgroundRect(Theme.Chrome);
            Tools(gui);
            Divider(gui, "tools");
            if (Button(gui, EditorIcons.Frame, "frame", Hint("Frame selected", "frameSelected"))) frameSelected();
            if (Button(gui, EditorIcons.Cube, "projection", "Toggle perspective / orthographic", viewport.IsOrthographic))
                viewport.ToggleProjection();
            Divider(gui, "view");
            Coordinates(gui);
            Snapping(gui);
            using (gui.Node().ExpandWidth().Enter()) { }
            MenuButton(gui, "Transform", ref transformMenu, ref transformAt);
            MenuButton(gui, "View", ref viewMenu, ref viewAt);
        }
        Menus(gui);
        Options(gui);
    }

    void Tools(Gui gui)
    {
        Tool(gui, TransformGizmoMode.Select, EditorIcons.Select, "Select");
        Tool(gui, TransformGizmoMode.Translate, EditorIcons.Move, "Move");
        Tool(gui, TransformGizmoMode.Rotate, EditorIcons.Rotate, "Rotate");
        Tool(gui, TransformGizmoMode.Scale, EditorIcons.Scale, "Scale");
        Tool(gui, TransformGizmoMode.Combined, EditorIcons.Transform, "All transforms");
    }

    void Tool(Gui gui, TransformGizmoMode mode, string icon, string hint)
    {
        if (Button(gui, icon, mode.ToString(), Hint(hint, mode.ToString().ToLowerInvariant()), viewport.Gizmo.Mode == mode))
            viewport.Gizmo.Mode = mode;
    }

    string Hint(string text, string command)
    {
        var binding = viewport.Settings.Navigation?.DisplayFor("gaya.turian.viewport." + command) ?? string.Empty;
        return binding.Length == 0 ? text : $"{text} ({binding})";
    }

    void Coordinates(Gui gui)
    {
        var center = viewport.Settings.Tools.CenterPivot;
        if (Button(gui, center ? "Center" : "Pivot", "pivot", "Switch selection centre / active object pivot", width: 54f))
        {
            viewport.Settings.Tools.CenterPivot = !center;
            Changed("sceneTransform");
        }
        var world = viewport.Gizmo.Space == TransformGizmoSpace.World;
        if (Button(gui, world ? "Global" : "Local", "space", "Switch global / local axes", width: 54f))
            viewport.Gizmo.Space = world ? TransformGizmoSpace.Local : TransformGizmoSpace.World;
    }

    void Snapping(Gui gui)
    {
        var settings = viewport.Settings.Tools;
        if (Button(gui, EditorIcons.Magnet, "snap", "Toggle transform snapping", settings.SnapEnabled))
        {
            settings.SnapEnabled = !settings.SnapEnabled;
            Changed("sceneTransform");
        }
        if (Button(gui, EditorIcons.CaretDown, "snapOptions", "Translation, rotation and scale snap intervals", width: 18f))
            OpenOptions(gui, camera: false);
    }

    static void Divider(Gui gui, string id)
    {
        using (gui.Node(1f, Theme.Scale(16f), "scene/toolbar/divider/" + id).Margin(5f, 0).Enter())
            gui.DrawBackgroundRect(Theme.Border);
    }

    static bool Button(Gui gui, string label, string id, string hint, bool selected = false, float width = 24f)
    {
        using (gui.Node(Theme.Scale(width), Theme.Scale(22f), "scene/toolbar/" + id).BlockInput()
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var inputPass = gui.Pass == Pass.Pass2Render;
            var hot = inputPass && gui.GetInteractable().OnHover();
            if (selected) gui.DrawBackgroundRect(Theme.AccentFill, 3f);
            else if (hot) gui.DrawBackgroundRect(Theme.Hover, 3f);
            gui.DrawText(label, Theme.Text(width > 30f ? 11f : 13f), selected ? Theme.Ink : Theme.InkDim);
            gui.Tooltip(gui.CurrentNode, hint);
            return hot && gui.GetInteractable().OnClick();
        }
    }

    void MenuButton(Gui gui, string title, ref bool open, ref Vector2 position)
    {
        if (!Button(gui, title + " " + EditorIcons.CaretDown, title, title + " options", width: title.Length * 7f + 24f))
            return;
        open = !open;
        position = new Vector2(gui.CurrentNode.Rect.BottomRight.X - Theme.Scale(210f), gui.CurrentNode.Rect.BottomRight.Y);
        if (title == "Transform") viewMenu = false;
        else transformMenu = false;
    }

    void Menus(Gui gui)
    {
        gui.CascadeMenu(ref transformMenu, transformAt, menu => menu
            .CheckItem("Local axes", () => viewport.Gizmo.Space == TransformGizmoSpace.Local,
                value => viewport.Gizmo.Space = value ? TransformGizmoSpace.Local : TransformGizmoSpace.World)
            .CheckItem("Snapping", () => viewport.Settings.Tools.SnapEnabled,
                value => SetSnapEnabled(value))
            .Separator()
            .Item("Snap options…", () => OpenOptions(gui, camera: false))
            .Item("Reset snap intervals", ResetSnapping), fontSize: Theme.Text(12));
        gui.CascadeMenu(ref viewMenu, viewAt, menu => BuildViewMenu(menu, gui), fontSize: Theme.Text(12));
    }

    void BuildViewMenu(FlyoutBuilder menu, Gui gui)
    {
        menu
            .Item("Frame selected", frameSelected, viewport.Settings.Navigation?.DisplayFor("gaya.turian.viewport.frameSelected") ?? "")
            .CheckItem("Orthographic", () => viewport.IsOrthographic, _ => viewport.ToggleProjection())
            .Separator();
        VisibilityItems(menu);
        menu.Submenu("Visible layers", sub => LayerItems(sub, visibility: true));
        menu.Submenu("Locked layers", sub => LayerItems(sub, visibility: false));
        menu.Separator().Item("Camera options…", () => OpenOptions(gui, camera: true));
    }

    void LayerItems(FlyoutBuilder menu, bool visibility)
    {
        var view = viewport.Settings.View;
        foreach (var slot in viewport.RenderLayers.OrderBy(slot => slot.Index))
        {
            if ((uint)slot.Index >= 32) continue;
            var index = slot.Index;
            menu.CheckItem($"{index}: {slot.Name}",
                () => (visibility ? view.VisibleLayers : view.LockedLayers).Contains(index), value =>
                {
                    if (visibility) view.SetVisible(index, value);
                    else view.SetLocked(index, value);
                    Changed("sceneViewer");
                });
        }
    }

    void VisibilityItems(FlyoutBuilder menu)
    {
        menu
            .CheckItem("Show grid", () => viewport.Settings.Grid.Visible, value =>
            {
                viewport.Settings.Grid.Visible = value;
                Changed("sceneGrid");
            })
            .CheckItem("Show gizmos", () => viewport.Settings.Gizmos.Visible, value =>
            {
                viewport.Settings.Gizmos.Visible = value;
                Changed("sceneGizmos");
            })
            .CheckItem("Show orientation", () => viewport.Settings.Gizmos.ShowOrientation,
                value =>
                {
                    viewport.Settings.Gizmos.ShowOrientation = value;
                    Changed("sceneGizmos");
                });
    }

    void ResetSnapping()
    {
        var settings = viewport.Settings.Tools;
        settings.TranslationSnap = 1f;
        settings.RotationSnap = 15f;
        settings.ScaleSnap = 0.1f;
        Changed("sceneTransform");
    }

    void SetSnapEnabled(bool value)
    {
        viewport.Settings.Tools.SnapEnabled = value;
        Changed("sceneTransform");
    }

    void Changed(string page) => viewport.Settings.NotifyChanged("gaya.turian." + page);

    void OpenOptions(Gui gui, bool camera)
    {
        snapOptions = !camera;
        cameraOptions = camera;
        var rect = toolbarBounds;
        optionsAt = new Vector2(Math.Clamp(gui.Input.MousePosition.X, rect.X,
            Math.Max(rect.X, rect.BottomRight.X - Theme.Scale(250f))), rect.BottomRight.Y + 2f);
        transformMenu = viewMenu = false;
    }

    void Options(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build)
        {
            frameOptionsOpen = snapOptions || cameraOptions;
            frameCameraOptions = cameraOptions;
            frameOptionsAt = optionsAt;
        }
        if (!frameOptionsOpen) return;
        var camera = frameCameraOptions;
        using (gui.Node(Theme.Scale(250f), Theme.Scale(camera ? 216f : 134f), "scene/options")
                   .AbsoluteScreen(frameOptionsAt.X, frameOptionsAt.Y).BlockInput().Direction(Axis.Vertical)
                   .Padding(10f).Gap(5f).Enter())
        {
            gui.SetZIndex(9500);
            gui.DrawBackgroundRect(Theme.Panel, 4f);
            gui.DrawRectBorder(gui.CurrentNode.Rect, Theme.Border);
            using (gui.Node().ExpandWidth().Direction(Axis.Horizontal).Enter())
            {
                using (gui.Node().ExpandWidth().Enter())
                    gui.DrawText(camera ? "Camera" : "Snapping", Theme.Text(12), Theme.Ink);
                if (Button(gui, EditorIcons.Xmark, "closeOptions", "Close options", width: 18f))
                    snapOptions = cameraOptions = false;
            }
            if (camera) CameraFields(gui);
            else SnapFields(gui);
            DismissOptions(gui);
        }
    }

    void CameraFields(Gui gui)
    {
        var settings = viewport.Settings;
        Field(gui, "Field of view (°)", "fov", settings.FieldOfView, value => settings.FieldOfView = Math.Clamp(value, 1, 120));
        Field(gui, "Near clip", "near", settings.NearClip, value => settings.NearClip = Math.Max(0.001f, value));
        Field(gui, "Far clip", "far", settings.FarClip, value => settings.FarClip = Math.Max(settings.NearClip + 0.01f, value));
        Field(gui, "Move speed", "speed", settings.MoveSpeed, value => settings.MoveSpeed = Math.Clamp(value, 0.1f, 100));
        Field(gui, "Look sensitivity", "look", settings.LookSensitivity,
            value => settings.LookSensitivity = Math.Clamp(value, 0.0005f, 0.05f));
    }

    void SnapFields(Gui gui)
    {
        var settings = viewport.Settings.Tools;
        Field(gui, "Move (units)", "move", settings.TranslationSnap, value => settings.TranslationSnap = Math.Max(0, value));
        Field(gui, "Rotate (°)", "rotate", settings.RotationSnap, value => settings.RotationSnap = Math.Max(0, value));
        Field(gui, "Scale", "scale", settings.ScaleSnap, value => settings.ScaleSnap = Math.Max(0, value));
    }

    void Field(Gui gui, string label, string id, float value, Action<float> change)
    {
        using (gui.Node(-1, Theme.Scale(24f), "scene/options/" + id).ExpandWidth()
                   .Direction(Axis.Horizontal).ContentAlignY(0.5f).Enter())
        {
            using (gui.Node().ExpandWidth().Enter()) gui.DrawText(label, Theme.Text(11), Theme.InkDim);
            var text = value.ToString("0.####", CultureInfo.InvariantCulture);
            string edited;
            using (gui.Node(Theme.Scale(80f), Theme.Scale(22f), "scene/options/value/" + id).Enter())
                edited = gui.TextInput(text, width: Theme.Scale(80f), height: Theme.Scale(22f), fontSize: Theme.Text(11),
                    backgroundColor: Theme.Field, borderColor: Theme.Border, textColor: Theme.Ink, padding: 4,
                    id: "scene/options/input/" + id, alignX: 1f);
            if (gui.Pass == Pass.Pass2Render && edited != text
                && float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && float.IsFinite(parsed))
            {
                change(parsed);
                viewport.Settings.NotifyChanged(frameCameraOptions ? "gaya.turian.editorCamera" : "gaya.turian.sceneTransform");
            }
        }
    }

    void DismissOptions(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render) return;
        if (gui.Input.IsKeyPressed(KeyboardKey.Escape)
            || gui.Input.IsMouseButtonPressed(MouseButton.Left) && !gui.CurrentNode.Rect.Contains(gui.Input.MousePosition))
            snapOptions = cameraOptions = false;
    }
}
