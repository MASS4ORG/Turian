namespace Turian.Engine.Core;

/// <summary>A time-limited ribbon recording the attached node's world-space motion.</summary>
[ComponentContextMenu("Rendering/Trail Renderer")]
[TypeId("d573915b-5bc1-4785-8bb3-697993a01f95")]
public sealed class TrailRenderer : LineRenderer
{
    readonly List<(Vector3 Position, double Time)> samples = [];
    double elapsed;

    /// <summary>Lifetime of a recorded point in simulation seconds; must be finite and positive.</summary>
    public float Time
    {
        get;
        set
        {
            CurveValidation.Finite(value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 1;

    /// <summary>Minimum world distance before another point is recorded.</summary>
    public float MinVertexDistance
    {
        get;
        set
        {
            CurveValidation.Finite(value);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 0.1f;

    /// <summary>Whether motion emits new points; existing points continue aging when false.</summary>
    public bool Emitting { get; set; } = true;

    /// <summary>Maximum retained points, bounding memory during very fast motion.</summary>
    public int MaxPoints
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            field = value;
        }
    } = 4096;

    /// <summary>Gets the number of live recorded points.</summary>
    [JsonIgnore]
    public int PositionCount => samples.Count;

    /// <summary>Clears the history immediately without changing emission.</summary>
    public void Clear() => samples.Clear();

    /// <inheritdoc/>
    public override void OnLateUpdate(float deltaTime)
    {
        CurveValidation.Finite(deltaTime);
        ArgumentOutOfRangeException.ThrowIfNegative(deltaTime);
        elapsed += deltaTime;
        samples.RemoveAll(sample => elapsed - sample.Time >= Time);
        if (!Emitting || Node is not { } node)
        {
            return;
        }

        var position = node.GlobalTransform.Position;
        if (samples.Count > 0 && Vector3.Distance(samples[^1].Position, position) < MinVertexDistance)
        {
            return;
        }

        if (samples.Count > 0 && samples[^1].Position == position)
        {
            return;
        }

        samples.Add((position, elapsed));
        if (samples.Count > MaxPoints)
        {
            samples.RemoveRange(0, samples.Count - MaxPoints);
        }
    }

    /// <summary>Builds a world-space trail; width and gradient run from newest at zero to expired at one.</summary>
    public override WorldGraphicGeometry BuildGeometry(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var points = samples.Select(static sample => sample.Position).ToArray();
        if (!UseWorldSpace && Node is { } node)
        {
            if (!Matrix4x4.Invert(node.GlobalTransform.Matrix4X4(), out var inverse))
            {
                return WorldGraphicGeometry.Empty;
            }

            for (var i = 0; i < points.Length; i++)
            {
                points[i] = Vector3.Transform(points[i], inverse);
            }
        }
        var times = samples.Select(sample => (float)Math.Clamp((elapsed - sample.Time) / Time, 0, 1)).ToArray();
        Array.Reverse(points);
        Array.Reverse(times);
        (points, times) = RibbonSampling.Subdivide(points, times, WidthCurve, ColorGradient);
        Array.Reverse(points);
        Array.Reverse(times);
        return BuildRibbon(points, times, camera);
    }

    /// <inheritdoc/>
    public override void OnDisable() => Clear();
}
