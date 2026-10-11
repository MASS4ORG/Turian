namespace Turian.Engine.Core;

/// <summary>
/// A renderable model: the submesh layout and bounds every host can read, with the GPU buffers owned by the
/// render backend that created it.
/// </summary>
public abstract class Model : IDisposable
{
    /// <summary>
    /// The submeshes that make up this model. Each submesh covers a contiguous
    /// range of the shared index buffer and may carry its own material.
    /// Always contains at least one submesh.
    /// </summary>
    public IReadOnlyList<SubMesh> SubMeshes { get; }

    /// <summary>Gets the model-space bounds enclosing all submeshes.</summary>
    public Bounds Bounds { get; }

    /// <summary>Takes the submesh layout and bounds of <paramref name="builder"/>.</summary>
    /// <param name="builder">The geometry the backend uploads.</param>
    protected Model(ModelBuilder builder)
    {
        SubMeshes = builder.GetBoundedSubMeshes();
        Bounds = SubMeshes.Aggregate(Bounds.Empty, static (bounds, sub) => bounds.Encapsulate(sub.Bounds));
    }

    /// <summary>Releases the backend resources held by the model.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the backend resources; <paramref name="disposing"/> is false from a finalizer.</summary>
    protected abstract void Dispose(bool disposing);
}
