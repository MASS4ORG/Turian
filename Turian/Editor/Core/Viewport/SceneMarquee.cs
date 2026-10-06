namespace Turian.Editor.Core;

/// <summary>Selects visible, unlocked model bounds intersecting a viewport rectangle.</summary>
public static class SceneMarquee
{
    /// <summary>Resolves a rectangle against the active models in the scene using the viewport's camera and layers.</summary>
    public static IReadOnlyList<Node> Pick(Node root, ICamera camera, Vector2 start, Vector2 end,
        Vector2 viewport, SceneViewSettings? settings = null) =>
        SelectBounds(ScenePicker.EnumerateWorldBounds(root), camera, start, end, viewport, settings);

    /// <summary>Tests world bounds after camera clipping, excluding inactive, invisible and locked nodes.</summary>
    public static IReadOnlyList<Node> SelectBounds(IEnumerable<(Node Node, Bounds WorldBounds)> candidates,
        ICamera camera, Vector2 start, Vector2 end, Vector2 viewport, SceneViewSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(camera);
        if (ClipSelectionRect(start, end, viewport) is not { } selection) return [];
        var matrix = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        var selected = new List<Node>();
        foreach (var (node, bounds) in candidates)
        {
            if (!CanSelect(node, settings)) continue;
            var projected = ProjectBounds(bounds, matrix, viewport);
            if (projected is { } rect && Intersects(rect, selection.Min, selection.Max)) selected.Add(node);
        }
        return [.. selected.Distinct()];
    }

    static (Vector2 Min, Vector2 Max)? ClipSelectionRect(Vector2 start, Vector2 end, Vector2 viewport)
    {
        if (viewport.X <= 0 || viewport.Y <= 0) return null;
        var min = Vector2.Max(Vector2.Min(start, end), Vector2.Zero);
        var max = Vector2.Min(Vector2.Max(start, end), viewport);
        return min.X > max.X || min.Y > max.Y ? null : (min, max);
    }

    static bool CanSelect(Node node, SceneViewSettings? settings) => node.IsActiveInHierarchy
        && settings?.CanSelect(node) != false && settings?.VisibleLayers.Contains(node.RenderLayer) != false;

    static bool Intersects((Vector2 Min, Vector2 Max) rect, Vector2 min, Vector2 max) =>
        rect.Min.X <= max.X && rect.Max.X >= min.X && rect.Min.Y <= max.Y && rect.Max.Y >= min.Y;

    static (Vector2 Min, Vector2 Max)? ProjectBounds(Bounds bounds, Matrix4x4 matrix, Vector2 viewport)
    {
        if (bounds.IsEmpty) return null;
        var corners = ProjectCorners(bounds, matrix);
        var points = new List<Vector2>();
        for (var i = 0; i < corners.Length; i++)
            for (var bit = 1; bit <= 4; bit *= 2)
                if ((i & bit) == 0) AddClippedEdge(points, corners[i], corners[i | bit], viewport);
        if (points.Count == 0) return null;
        var min = points[0];
        var max = min;
        foreach (var point in points)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return (min, max);
    }

    static Vector4[] ProjectCorners(Bounds bounds, Matrix4x4 matrix)
    {
        var corners = new Vector4[8];
        for (var i = 0; i < corners.Length; i++)
            corners[i] = Vector4.Transform(new Vector4(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z, 1), matrix);
        return corners;
    }

    static void AddClippedEdge(List<Vector2> points, Vector4 a, Vector4 b, Vector2 viewport)
    {
        if (!Clip(ref a, ref b, a.Z, b.Z) || !Clip(ref a, ref b, a.W - a.Z, b.W - b.Z)) return;
        AddPoint(points, a, viewport);
        AddPoint(points, b, viewport);
    }

    static bool Clip(ref Vector4 a, ref Vector4 b, float distanceA, float distanceB)
    {
        if (distanceA < 0 && distanceB < 0) return false;
        if (distanceA >= 0 && distanceB >= 0) return true;
        var intersection = Vector4.Lerp(a, b, distanceA / (distanceA - distanceB));
        if (distanceA < 0) a = intersection;
        else b = intersection;
        return true;
    }

    static void AddPoint(List<Vector2> points, Vector4 clip, Vector2 viewport)
    {
        if (clip.W <= 0) return;
        var point = new Vector2(clip.X, clip.Y) / clip.W;
        points.Add((point + Vector2.One) * 0.5f * viewport);
    }
}
