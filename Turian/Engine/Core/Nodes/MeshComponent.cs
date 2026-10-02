namespace Turian.Engine.Core;

/// <summary>
/// Reusable mesh rendering behavior that can be attached to any <see cref="Node"/>.
/// </summary>
[ComponentContextMenu("Rendering/Mesh")]
[TypeId("439a17b6-0567-576b-9dcd-9404bc53e726")]
public class MeshComponent : Component, IDisposable
{
    ExtMeshShader extMeshShader = null!;
    bool hasMeshShaderExtension;

    /// <summary>The render device bound to this scene, or <c>null</c> in a non-rendering host.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public Vulkan? RenderDevice { get; private set; }

    /// <summary>
    /// Initializes the component by resolving the runtime Vulkan service when available.
    /// </summary>
    public override void OnAwake()
    {
        base.OnAwake();

        if (RenderDevice is not { } vulkan)
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
    public Matrix4x4 TransformationMatrix() => Node?.Transform.Matrix4X4() ?? Matrix4x4.Identity;

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
