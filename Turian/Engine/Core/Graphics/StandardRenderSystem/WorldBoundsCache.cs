namespace Turian.Engine.Core;

/// <summary>Caches a component's matrices and submesh bounds until its model, range or global transform changes.</summary>
sealed class WorldBoundsCache
{
    Model? model;
    Transform transform;
    int start;
    int count;

    /// <summary>The model-to-world matrix for the cached transform.</summary>
    public Matrix4x4 ModelMatrix { get; private set; }

    /// <summary>The normal matrix for the cached transform.</summary>
    public Matrix4x4 NormalMatrix { get; private set; }

    /// <summary>The world bounds of the selected submesh range, in range order.</summary>
    public Bounds[] Bounds { get; private set; } = [];

    /// <summary>Refreshes the selected range when the model, range or cached global transform changes.</summary>
    public void Update(Model source, Transform global, int rangeStart, int rangeCount)
    {
        if (ReferenceEquals(model, source) && transform == global && start == rangeStart && count == rangeCount)
            return;

        model = source;
        transform = global;
        start = rangeStart;
        count = rangeCount;
        ModelMatrix = global.Matrix4X4();
        NormalMatrix = global.NormalMatrix();
        if (Bounds.Length != count) Bounds = new Bounds[count];
        for (var i = 0; i < count; i++)
            Bounds[i] = ModelBoundsUtility.ToWorldBounds(source.SubMeshes[start + i].Bounds, ModelMatrix);
    }
}
