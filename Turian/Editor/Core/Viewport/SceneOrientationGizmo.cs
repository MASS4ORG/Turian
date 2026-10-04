namespace Turian.Editor.Core;

/// <summary>Projects a cube and signed world axes for the Scene view's camera orientation control.</summary>
public static class SceneOrientationGizmo
{
    /// <summary>The orientation control's width and height in pixels.</summary>
    public const float Size = 80f;

    /// <summary>A world axis endpoint, projected relative to the widget center.</summary>
    public readonly record struct Marker(Vector3 Direction, Vector2 Offset, float Depth, string Label)
    {
        /// <summary>The endpoint's radius in pixels, including perspective foreshortening.</summary>
        public float Radius { get; init; } = 8f;

        /// <summary>Whether the endpoint projects outside the central cube.</summary>
        public bool IsVisible => Offset.LengthSquared() >= 10f * 10f;
    }

    /// <summary>A visible cube face with projected corners and a shading level.</summary>
    public readonly record struct Face(Vector2 A, Vector2 B, Vector2 C, Vector2 D, float Shade);

    /// <summary>Gets signed axes ordered from back to front.</summary>
    public static Marker[] Markers(EditorCamera camera)
    {
        var axes = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ };
        return [.. axes.Select(axis => CreateMarker(camera, axis))
            .OrderByDescending(marker => marker.Depth)];
    }

    static Marker CreateMarker(EditorCamera camera, Vector3 direction)
    {
        var depth = Vector3.Dot(direction, camera.Front);
        var perspective = camera.IsOrthographic ? 1f : 1f / (1f + depth * 0.3f);
        return new Marker(direction, Project(camera, direction) * 25f * perspective, depth, Label(direction))
        {
            Radius = 8f * perspective
        };
    }

    /// <summary>Gets the visible faces of the central cube in the current projection.</summary>
    public static Face[] Faces(EditorCamera camera)
    {
        var size = camera.IsOrthographic ? 3.8f : 4.5f;
        var faces = new List<Face>(3);
        Add(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0.82f);
        Add(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, 1f);
        Add(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0.65f);
        return [.. faces];

        void Add(Vector3 normal, Vector3 u, Vector3 v, float shade)
        {
            var facing = Vector3.Dot(normal, camera.Front);
            if (MathF.Abs(facing) < 1e-5f) return;
            normal *= -MathF.Sign(facing);
            faces.Add(new Face(Project(camera, normal - u - v) * size,
                Project(camera, normal + u - v) * size, Project(camera, normal + u + v) * size,
                Project(camera, normal - u + v) * size, shade));
        }
    }

    /// <summary>Returns the signed axis beneath the pointer, zero for the center, or null outside a handle.</summary>
    public static Vector3? HitTest(EditorCamera camera, Vector2 offset)
    {
        foreach (var marker in Markers(camera).Reverse())
            if (marker.IsVisible
                && Vector2.DistanceSquared(marker.Offset, offset) <= (marker.Radius + 2f) * (marker.Radius + 2f))
                return marker.Direction;
        return offset.LengthSquared() <= 10f * 10f ? Vector3.Zero : null;
    }

    static Vector2 Project(EditorCamera camera, Vector3 direction) =>
        new(Vector3.Dot(direction, camera.Right),
            Vector3.Dot(direction, camera.Up) * MathF.Sign(camera.GetProjectionMatrix().M22));

    static string Label(Vector3 direction)
    {
        var letter = MathF.Abs(direction.X) > 0.5f ? "X" : MathF.Abs(direction.Y) > 0.5f ? "Y" : "Z";
        return Vector3.Dot(direction, Vector3.One) > 0f ? letter : "−" + letter;
    }
}
