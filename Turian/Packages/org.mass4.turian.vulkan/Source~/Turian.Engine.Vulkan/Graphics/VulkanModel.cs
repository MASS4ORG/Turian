namespace Turian.Engine.Core;

/// <summary>
/// A <see cref="Model"/> whose vertex and index data live in Vulkan device-local buffers.
/// </summary>
public class VulkanModel : Model
{
    readonly Vulkan vulkan;
    Buffer vertexBuffer = null!;
    readonly uint vertexCount;
    readonly bool hasIndexBuffer;
    Buffer indexBuffer = null!;
    readonly uint indexCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="VulkanModel"/> class.
    /// </summary>
    /// <param name="vulkan">The Vulkan context.</param>
    /// <param name="builder">A model builder for creating the model.</param>
    public VulkanModel(Vulkan vulkan, ModelBuilder builder) : base(builder)
    {
        this.vulkan = vulkan;
        vertexCount = (uint)builder.Vertices.Length;
        CreateVertexBuffers(builder.Vertices);
        indexCount = (uint)builder.Indices.Length;
        if (indexCount > 0)
        {
            hasIndexBuffer = true;
            CreateIndexBuffers(builder.Indices);
        }
    }

    /// <summary>Whether draws use the index buffer, which decides the indirect command layout.</summary>
    public bool IsIndexed => hasIndexBuffer;

    void CreateVertexBuffers(Vertex[] vertices)
    {
        var instanceSize = (ulong)Vertex.SizeOf();
        var bufferSize = instanceSize * (ulong)vertices.Length;

        using Buffer stagingBuffer =
            new(
                vulkan,
                instanceSize,
                vertexCount,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            );
        _ = stagingBuffer.Map();
        stagingBuffer.WriteToBuffer(vertices);

        vertexBuffer = new(
            vulkan,
            instanceSize,
            vertexCount,
            BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit
        );

        vulkan.Device.CopyBuffer(stagingBuffer.VkBuffer, vertexBuffer.VkBuffer, bufferSize);
    }

    void CreateIndexBuffers(uint[] indices)
    {
        var instanceSize = (ulong)Unsafe.SizeOf<uint>();
        var bufferSize = instanceSize * (ulong)indices.Length;

        using Buffer stagingBuffer =
            new(
                vulkan,
                instanceSize,
                indexCount,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            );
        _ = stagingBuffer.Map();
        stagingBuffer.WriteToBuffer(indices);

        indexBuffer = new(
            vulkan,
            instanceSize,
            indexCount,
            BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit
        );

        vulkan.Device.CopyBuffer(stagingBuffer.VkBuffer, indexBuffer.VkBuffer, bufferSize);
    }

    /// <summary>
    /// Binds the model's vertex and index buffers to a Vulkan command buffer for rendering.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer.</param>
    public unsafe void Bind(CommandBuffer commandBuffer)
    {
        var vertexBufferHandle = vertexBuffer.VkBuffer;
        ulong offset = 0;
        vulkan.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBufferHandle, &offset);

        if (hasIndexBuffer)
        {
            vulkan.Vk.CmdBindIndexBuffer(commandBuffer, indexBuffer.VkBuffer, 0, IndexType.Uint32);
        }
    }

    /// <summary>
    /// Draws all submeshes of the model. Equivalent to calling
    /// <see cref="DrawSubMesh(CommandBuffer, int)"/> for each entry in <see cref="Model.SubMeshes"/>.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer.</param>
    public void Draw(CommandBuffer commandBuffer)
    {
        for (var i = 0; i < SubMeshes.Count; i++)
            DrawSubMesh(commandBuffer, i);
    }

    /// <summary>
    /// Draws the submesh at <paramref name="index"/>. Bind the model and the submesh's
    /// material descriptor sets first.
    /// </summary>
    public void DrawSubMesh(CommandBuffer commandBuffer, int index) => DrawSubMesh(commandBuffer, index, 1, 0);

    /// <summary>
    /// Draws <paramref name="instanceCount"/> instances of the submesh at <paramref name="index"/>; the shader sees
    /// <c>gl_InstanceIndex</c> starting at <paramref name="firstInstance"/>.
    /// </summary>
    public void DrawSubMesh(CommandBuffer commandBuffer, int index, uint instanceCount, uint firstInstance)
    {
        var sub = SubMeshes[index];
        if (hasIndexBuffer)
        {
            vulkan.Vk.CmdDrawIndexed(commandBuffer, sub.IndexCount, instanceCount, sub.IndexStart, 0, firstInstance);
        }
        else
        {
            vulkan.Vk.CmdDraw(commandBuffer, sub.IndexCount, instanceCount, sub.IndexStart, firstInstance);
        }
    }

    /// <summary>Draws one GPU-written command; indexed and non-indexed models use a common 20-byte stride.</summary>
    public void DrawSubMeshIndirect(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer commands, ulong offset)
    {
        if (hasIndexBuffer)
            vulkan.Vk.CmdDrawIndexedIndirect(commandBuffer, commands, offset, 1, 20);
        else
            vulkan.Vk.CmdDrawIndirect(commandBuffer, commands, offset, 1, 20);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        vertexBuffer.Dispose();
        if (hasIndexBuffer) indexBuffer.Dispose();
    }
}
