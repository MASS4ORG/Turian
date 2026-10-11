namespace Turian.Engine.Core;

/// <summary>
/// Reusable mesh rendering behavior that can be attached to any <see cref="Node"/>. A render backend with mesh
/// shaders draws it; other hosts ignore it.
/// </summary>
[ComponentContextMenu("Rendering/Mesh")]
[TypeId("439a17b6-0567-576b-9dcd-9404bc53e726")]
public class MeshComponent : Component, IDisposable
{
    /// <summary>
    /// Calculates and returns the transformation matrix for the owning node.
    /// </summary>
    /// <returns>The transformation matrix.</returns>
    public Matrix4x4 TransformationMatrix() => Node?.Transform.Matrix4X4() ?? Matrix4x4.Identity;

    /// <summary>
    /// Disposes of the mesh component.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
