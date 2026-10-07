namespace Gaya.Plugin.Turian;

/// <summary>Draws the orientation control and routes its clicks to the camera controller.</summary>
sealed class SceneOrientationWidget
{
    readonly EditorCamera initialCamera = new();
    SceneGizmoSettings settings = new();
    bool frameVisible;

    /// <summary>Draws stable layout nodes for the cube and signed axes in both GUI passes.</summary>
    public void Render(Gui gui, SceneCameraController? controller, Node? selected, SceneGizmoSettings? preferences = null)
    {
        if (!BeginFrame(gui, preferences)) return;
        using (gui.Node(-1, 104, "scene/orientationHost").ExpandWidth().Absolute(0, 0)
                   .Direction(Axis.Vertical).ContentAlignX(1f).ContentAlignY(0f).Padding(12).Enter())
        using (gui.Node(SceneOrientationGizmo.Size, SceneOrientationGizmo.Size, "scene/orientation")
                   .BlockInput().Enter())
        {
            gui.SetZIndex(10);
            var camera = controller?.Camera ?? initialCamera;
            var center = gui.CurrentNode.Rect.Center;
            var hovered = gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnHover();
            var hit = hovered ? SceneOrientationGizmo.HitTest(camera, gui.Input.MousePosition - center) : null;
            gui.DrawCircle(center, 35, hovered
                ? ThemeTokens.Current.GetColor(ViewportThemeTokens.OrientationBackgroundHover,
                    GuiColor.FromArgb(178, 31, 36, 43))
                : ThemeTokens.Current.GetColor(ViewportThemeTokens.OrientationBackground,
                    GuiColor.FromArgb(56, 31, 36, 43)));
            DrawContents(gui, center, camera, hit);
            ProcessClick(gui, controller, selected, hovered, hit);
        }
    }

    bool BeginFrame(Gui gui, SceneGizmoSettings? preferences)
    {
        settings = preferences ?? settings;
        if (gui.Pass == Pass.Pass1Build) frameVisible = settings.ShowOrientation;
        return frameVisible;
    }

    void DrawContents(Gui gui, Vector2 center, EditorCamera camera, Vector3? hit)
    {
        var markers = SceneOrientationGizmo.Markers(camera);
        foreach (var marker in markers.Where(marker => marker.Depth >= 0))
            DrawMarker(gui, center, marker, hit);
        DrawCube(gui, center, camera, hit == Vector3.Zero);
        foreach (var marker in markers.Where(marker => marker.Depth < 0))
            DrawMarker(gui, center, marker, hit);
        foreach (var marker in markers) DrawLabel(gui, marker, hit);
    }

    static void ProcessClick(Gui gui, SceneCameraController? controller, Node? selected, bool hovered, Vector3? hit)
    {
        if (hovered && gui.Input.IsMouseButtonPressed(MouseButton.Left) && hit is { } direction)
            Activate(controller, direction, selected);
    }

    static void Activate(SceneCameraController? controller, Vector3 direction, Node? selected)
    {
        if (controller is null) return;
        var pivot = selected?.GlobalTransform.Position;
        if (direction == Vector3.Zero) controller.ToggleProjection(pivot);
        else controller.AlignToAxis(direction, pivot);
    }

    static void DrawCube(Gui gui, Vector2 center, EditorCamera camera, bool hovered)
    {
        var lit = ThemeTokens.Current.GetColor(ViewportThemeTokens.OrientationCube,
            GuiColor.FromArgb(255, 209, 219, 235));
        foreach (var face in SceneOrientationGizmo.Faces(camera))
        {
            var shade = hovered ? 1f : face.Shade;
            var color = GuiColor.FromArgb(255, (int)(shade * lit.R), (int)(shade * lit.G), (int)(shade * lit.B));
            gui.DrawTriangle(center + face.A, center + face.B, center + face.C, color);
            gui.DrawTriangle(center + face.A, center + face.C, center + face.D, color);
        }
    }

    void DrawMarker(Gui gui, Vector2 center, SceneOrientationGizmo.Marker marker, Vector3? hit)
    {
        if (!marker.IsVisible) return;
        var positive = Vector3.Dot(marker.Direction, Vector3.One) > 0;
        var color = AxisColor(marker.Direction);
        if (!positive) color = GuiColor.Lerp(color, GuiColor.White, 0.5f);
        gui.DrawLine(center, center + marker.Offset, color, positive ? 2f : 1.5f);
        if (hit == marker.Direction)
            gui.DrawCircle(center + marker.Offset, marker.Radius + 1.5f, ThemeTokens.Current.Ink);
        gui.DrawCircle(center + marker.Offset, marker.Radius, color);
    }

    static void DrawLabel(Gui gui, SceneOrientationGizmo.Marker marker, Vector3? hit)
    {
        var visible = marker.IsVisible;
        var positive = Vector3.Dot(marker.Direction, Vector3.One) > 0;
        using (gui.Node(24, 20, "scene/orientation/" + marker.Label)
                   .Absolute(SceneOrientationGizmo.Size / 2f - 12f + marker.Offset.X,
                       SceneOrientationGizmo.Size / 2f - 10f + marker.Offset.Y)
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
            gui.DrawText(visible && (positive || hit == marker.Direction) ? marker.Label : string.Empty,
                ThemeTokens.Current.Text(Math.Clamp(marker.Radius * 1.25f, 8f, 11f)), ThemeTokens.Current.Chrome);
    }

    GuiColor AxisColor(Vector3 direction)
    {
        var color = settings.AxisColor(direction);
        return GuiColor.FromArgb(255, (int)(color.X * 255), (int)(color.Y * 255), (int)(color.Z * 255));
    }
}
