namespace Gaya.Plugin.Turian;

/// <summary>Draws the orientation control and routes its clicks to the camera controller.</summary>
sealed class SceneOrientationWidget
{
    readonly EditorCamera initialCamera = new();

    /// <summary>Draws stable layout nodes for the cube and signed axes in both GUI passes.</summary>
    public void Render(Gui gui, SceneCameraController? controller, Node? selected)
    {
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
            gui.DrawCircle(center, 35, GuiColor.FromArgb(hovered ? 178 : 56, 31, 36, 43));
            DrawContents(gui, center, camera, hit);
            ProcessClick(gui, controller, selected, hovered, hit);
        }
    }

    static void DrawContents(Gui gui, Vector2 center, EditorCamera camera, Vector3? hit)
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
        foreach (var face in SceneOrientationGizmo.Faces(camera))
        {
            var shade = hovered ? 1f : face.Shade;
            var color = GuiColor.FromArgb(255, (int)(shade * 209), (int)(shade * 219), (int)(shade * 235));
            gui.DrawTriangle(center + face.A, center + face.B, center + face.C, color);
            gui.DrawTriangle(center + face.A, center + face.C, center + face.D, color);
        }
    }

    static void DrawMarker(Gui gui, Vector2 center, SceneOrientationGizmo.Marker marker, Vector3? hit)
    {
        if (!marker.IsVisible) return;
        var positive = Vector3.Dot(marker.Direction, Vector3.One) > 0;
        var color = AxisColor(marker.Direction);
        if (!positive) color = GuiColor.Lerp(color, GuiColor.White, 0.5f);
        gui.DrawLine(center, center + marker.Offset, color, positive ? 2f : 1.5f);
        if (hit == marker.Direction)
            gui.DrawCircle(center + marker.Offset, marker.Radius + 1.5f, StudioTheme.Current.Ink);
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
                StudioTheme.Current.Text(Math.Clamp(marker.Radius * 1.25f, 8f, 11f)), StudioTheme.Current.Chrome);
    }

    static GuiColor AxisColor(Vector3 direction) =>
        GuiColor.FromArgb(255,
            (int)((MathF.Abs(direction.X) * 0.96f + MathF.Abs(direction.Y) * 0.35f
                + MathF.Abs(direction.Z) * 0.28f) * 255),
            (int)((MathF.Abs(direction.X) * 0.36f + MathF.Abs(direction.Y) * 0.89f
                + MathF.Abs(direction.Z) * 0.55f) * 255),
            (int)((MathF.Abs(direction.X) * 0.4f + MathF.Abs(direction.Y) * 0.55f
                + MathF.Abs(direction.Z)) * 255));
}
