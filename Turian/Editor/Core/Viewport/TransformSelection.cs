namespace Turian.Editor.Core;

/// <summary>A transform gesture over selected roots, applied from snapshots to avoid accumulating errors.</summary>
public sealed class TransformSelection
{
    readonly (Node Node, Transform World, Vector3 LocalScale)[] snapshots;

    /// <summary>Captures selected roots and the gesture's shared pivot and axis orientation.</summary>
    public TransformSelection(IEnumerable<Node> nodes, Vector3 pivot, Quaternion axes)
    {
        snapshots = [.. SelectionService.TopLevelNodes(nodes)
            .Select(node => (node, node.GlobalTransform, node.Scale))];
        Pivot = pivot;
        Axes = axes;
    }

    /// <summary>The world position around which rotation and scaling occur.</summary>
    public Vector3 Pivot { get; }

    /// <summary>The orientation used to express scale offsets in the gesture's axes.</summary>
    public Quaternion Axes { get; }

    /// <summary>Translates each root by the same world-space displacement.</summary>
    public void Translate(Vector3 delta)
    {
        foreach (var (node, world, _) in snapshots)
            node.GlobalTransform = world with { Position = world.Position + delta };
    }

    /// <summary>Rotates positions and orientations around the shared pivot.</summary>
    public void Rotate(Quaternion rotation)
    {
        foreach (var (node, world, _) in snapshots)
            node.GlobalTransform = world with
            {
                Position = Pivot + Vector3.Transform(world.Position - Pivot, rotation),
                Orientation = Quaternion.Normalize(rotation * world.Orientation),
            };
    }

    /// <summary>Scales offsets in the gesture's axes and each root's original local scale.</summary>
    public void Scale(Vector3 factors)
    {
        var inverse = Quaternion.Inverse(Axes);
        foreach (var (node, world, localScale) in snapshots)
        {
            var offset = Vector3.Transform(world.Position - Pivot, inverse) * factors;
            node.GlobalTransform = world with { Position = Pivot + Vector3.Transform(offset, Axes) };
            node.Scale = localScale * factors;
        }
    }

    /// <summary>Gets the centre of the selected root positions, or the active object's pivot.</summary>
    public static Vector3 GetPivot(IReadOnlyList<Node> nodes, Node? active, bool center)
    {
        if (!center || nodes.Count == 0) return active?.GlobalTransform.Position ?? Vector3.Zero;
        var bounds = Bounds.Empty;
        foreach (var node in nodes) bounds = bounds.Encapsulate(node.GlobalTransform.Position);
        return bounds.Center;
    }
}
