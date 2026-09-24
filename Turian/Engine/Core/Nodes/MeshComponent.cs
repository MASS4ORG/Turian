namespace Turian.Engine.Core;

/// <summary>
/// Reusable mesh rendering behavior that can be attached to any <see cref="Node"/>.
/// </summary>
[ComponentContextMenu("Rendering/Mesh")]
[TypeId("a3000001-0000-4000-8000-000000000007")]
public class MeshComponent : Component, IDisposable
{
    ExtMeshShader extMeshShader = null!;
    bool hasMeshShaderExtension;

    /// <summary>
    /// Initializes the component by resolving the runtime Vulkan service when available.
    /// </summary>
    public override void OnAwake()
    {
        base.OnAwake();

        var vulkan = RuntimeServices.TryGet<Vulkan>();
        if (vulkan is null)
        {
            hasMeshShaderExtension = false;
            return;
        }

        hasMeshShaderExtension = vulkan.Vk.TryGetDeviceExtension(
            vulkan.Device.Instance,
            vulkan.Device.VkDevice,
            out extMeshShader
        );
    }

    /// <summary>
    /// Calculates and returns the transformation matrix for the owning node.
    /// </summary>
    /// <returns>The transformation matrix.</returns>
    public Matrix4x4 TransformationMatrix()
    {
        var transform = Node?.Transform;
        if (transform is null) return Matrix4x4.Identity;
        var c3 = MathF.Cos(transform.Rotation.Z);
        var s3 = MathF.Sin(transform.Rotation.Z);
        var c2 = MathF.Cos(transform.Rotation.X);
        var s2 = MathF.Sin(transform.Rotation.X);
        var c1 = MathF.Cos(transform.Rotation.Y);
        var s1 = MathF.Sin(transform.Rotation.Y);

        return new(
            transform.Scale.X * ((c1 * c3) + (s1 * s2 * s3)),
            transform.Scale.X * (c2 * s3),
            transform.Scale.X * ((c1 * s2 * s3) - (c3 * s1)),
            0.0f,
            transform.Scale.Y * ((c3 * s1 * s2) - (c1 * s3)),
            transform.Scale.Y * (c2 * c3),
            transform.Scale.Y * ((c1 * c3 * s2) + (s1 * s3)),
            0.0f,
            transform.Scale.Z * (c2 * s1),
            transform.Scale.Z * (-s2),
            transform.Scale.Z * (c1 * c2),
            0.0f,
            transform.Position.X,
            transform.Position.Y,
            transform.Position.Z,
            1.0f
        );
    }

    /// <summary>
    /// Binds the mesh component to a command buffer for rendering.
    /// </summary>
    /// <param name="_">The Vulkan command buffer.</param>
    public static void Bind(CommandBuffer _) { }

    /// <summary>
    /// Draws the mesh using the provided command buffer.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer.</param>
    public void Draw(CommandBuffer commandBuffer)
    {
        if (!hasMeshShaderExtension)
        {
            return;
        }

        extMeshShader.CmdDrawMeshTask(commandBuffer, 1, 1, 1);
    }

    /// <summary>
    /// Disposes of the mesh component.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
