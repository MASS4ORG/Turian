namespace Turian.Engine.Core;

/// <summary>Builds double-sided polyline ribbons with sampled widths, RGB colors and distance-based UVs.</summary>
public static class LineRendererGeometry
{
    const float Epsilon = 1e-6f;

    /// <summary>Builds a ribbon in the supplied positions' coordinate space; consecutive duplicate points are skipped.</summary>
    /// <param name="positions">Polyline points in model or world space, as chosen by the caller.</param>
    /// <param name="normal">The ribbon's facing direction; joins limit their extension to five half-widths.</param>
    /// <param name="widths">No samples uses width one; otherwise supply one width or one per position.</param>
    /// <param name="colors">No samples uses white; otherwise supply one RGB color or one per position.</param>
    /// <param name="textureMode">Whether longitudinal UVs stretch across the line or tile per length unit.</param>
    public static ModelBuilder Build(ReadOnlySpan<Vector3> positions, Vector3 normal,
        ReadOnlySpan<float> widths = default, ReadOnlySpan<Vector3> colors = default,
        LineTextureMode textureMode = LineTextureMode.Stretch)
    {
        ValidateSamples(positions, widths, colors);
        ValidateNormal(normal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)textureMode, (uint)LineTextureMode.Tile);
        normal = Vector3.Normalize(normal);
        var retained = RetainPoints(positions);
        if (retained.Count < 2)
        {
            return new ModelBuilder();
        }

        var distances = Distances(positions, retained);
        var vertices = new Vertex[retained.Count * 2];
        for (var i = 0; i < retained.Count; i++)
        {
            var source = retained[i];
            var side = Join(positions, retained, i, normal);
            var width = widths.IsEmpty ? 1f : widths[widths.Length == 1 ? 0 : source];
            var color = colors.IsEmpty ? Vector3.One : colors[colors.Length == 1 ? 0 : source];
            var u = textureMode == LineTextureMode.Tile ? distances[i] : distances[i] / distances[^1];
            vertices[i * 2] = MakeVertex(positions[source] - side * (width * 0.5f), normal, color, new Vector2(u, 0));
            vertices[i * 2 + 1] = MakeVertex(positions[source] + side * (width * 0.5f), normal, color, new Vector2(u, 1));
        }

        return new ModelBuilder { Vertices = vertices, Indices = Indices(retained.Count - 1) };
    }

    static void ValidateSamples(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> widths,
        ReadOnlySpan<Vector3> colors)
    {
        ValidateCount(widths.Length, positions.Length, nameof(widths));
        ValidateCount(colors.Length, positions.Length, nameof(colors));
        foreach (var point in positions)
        {
            ValidateVector(point, nameof(positions));
        }

        foreach (var width in widths)
        {
            if (!float.IsFinite(width))
            {
                throw new ArgumentException("Line widths must be finite.", nameof(widths));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(width, nameof(widths));
        }

        foreach (var color in colors)
        {
            ValidateVector(color, nameof(colors));
        }
    }

    static void ValidateCount(int count, int points, string name)
    {
        if (count != 0 && count != 1 && count != points)
        {
            throw new ArgumentException("Supply no samples, one shared sample or one sample per position.", name);
        }
    }

    static void ValidateVector(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentException("Line vectors must be finite.", name);
        }
    }

    static void ValidateNormal(Vector3 normal)
    {
        ValidateVector(normal, nameof(normal));
        var squared = normal.LengthSquared();
        if (!float.IsFinite(squared) || squared < Epsilon * Epsilon)
        {
            throw new ArgumentException("A ribbon needs a finite, nonzero facing direction.", nameof(normal));
        }
    }

    static List<int> RetainPoints(ReadOnlySpan<Vector3> positions)
    {
        var retained = new List<int>(positions.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            if (retained.Count == 0 || Vector3.DistanceSquared(positions[retained[^1]], positions[i]) > Epsilon * Epsilon)
            {
                retained.Add(i);
            }
        }

        return retained;
    }

    static float[] Distances(ReadOnlySpan<Vector3> positions, List<int> retained)
    {
        var distances = new float[retained.Count];
        for (var i = 1; i < distances.Length; i++)
        {
            distances[i] = distances[i - 1] + Vector3.Distance(positions[retained[i - 1]], positions[retained[i]]);
            if (!float.IsFinite(distances[i]))
            {
                throw new ArgumentException("Line lengths must fit a finite float.", nameof(positions));
            }
        }

        return distances;
    }

    static Vector3 Join(ReadOnlySpan<Vector3> positions, List<int> retained, int i, Vector3 normal)
    {
        var point = positions[retained[i]];
        var before = i > 0 ? point - positions[retained[i - 1]] : positions[retained[1]] - point;
        var after = i + 1 < retained.Count ? positions[retained[i + 1]] - point : before;
        var incoming = Side(before, normal);
        var outgoing = Side(after, normal);
        var joined = incoming + outgoing;
        var side = joined.LengthSquared() > Epsilon * Epsilon ? Vector3.Normalize(joined) : outgoing;
        return side / MathF.Max(0.2f, MathF.Abs(Vector3.Dot(side, outgoing)));
    }

    static Vector3 Side(Vector3 tangent, Vector3 normal)
    {
        var direction = Vector3.Normalize(tangent);
        var side = Vector3.Cross(normal, direction);
        if (side.LengthSquared() < Epsilon * Epsilon)
        {
            var axis = MathF.Abs(direction.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            side = Vector3.Cross(axis, direction);
        }

        return Vector3.Normalize(side);
    }

    static Vertex MakeVertex(Vector3 point, Vector3 normal, Vector3 color, Vector2 uv)
    {
        ValidateVector(point, nameof(point));
        return new Vertex(point, color) { Normal = normal, Uv = uv };
    }

    static uint[] Indices(int segments)
    {
        var indices = new uint[segments * 12];
        for (var i = 0; i < segments; i++)
        {
            var a = (uint)(i * 2);
            var start = i * 12;
            ReadOnlySpan<uint> quad = [a, a + 1, a + 2, a + 1, a + 3, a + 2, a + 2, a + 1, a, a + 2, a + 3, a + 1];
            quad.CopyTo(indices.AsSpan(start));
        }

        return indices;
    }
}
