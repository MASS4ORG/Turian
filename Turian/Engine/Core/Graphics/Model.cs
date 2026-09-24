namespace Turian.Engine.Core;

/// <summary>
/// Represents a 3D model for rendering using Vulkan graphics API.
/// </summary>
public class Model : IDisposable
{
    readonly Vulkan vulkan;
    Buffer vertexBuffer = null!;
    readonly uint vertexCount;
    readonly bool hasIndexBuffer;
    Buffer indexBuffer = null!;
    readonly uint indexCount;

    /// <summary>
    /// The submeshes that make up this model. Each submesh covers a contiguous
    /// range of the shared index buffer and may carry its own material.
    /// Always contains at least one submesh.
    /// </summary>
    public IReadOnlyList<SubMesh> SubMeshes { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Model"/> class.
    /// </summary>
    /// <param name="vulkan">The Vulkan context.</param>
    /// <param name="builder">A model builder for creating the model.</param>
    public Model(Vulkan vulkan, ModelBuilder builder)
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

        SubMeshes = builder.SubMeshes is { Count: > 0 }
            ? builder.SubMeshes.AsReadOnly()
            : (IReadOnlyList<SubMesh>)[new SubMesh(0, indexCount > 0 ? indexCount : vertexCount)];
    }

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
        Silk.NET.Vulkan.Buffer[] vertexBuffers = [vertexBuffer.VkBuffer];
        ulong[] offsets = [0];

        fixed (ulong* offsetsPtr = offsets)
        fixed (Silk.NET.Vulkan.Buffer* vertexBuffersPtr = vertexBuffers)
        {
            vulkan.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, vertexBuffersPtr, offsetsPtr);
        }

        if (hasIndexBuffer)
        {
            vulkan.Vk.CmdBindIndexBuffer(commandBuffer, indexBuffer.VkBuffer, 0, IndexType.Uint32);
        }
    }

    /// <summary>
    /// Draws all submeshes of the model. Equivalent to calling
    /// <see cref="DrawSubMesh"/> for each entry in <see cref="SubMeshes"/>.
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
    public void DrawSubMesh(CommandBuffer commandBuffer, int index)
    {
        var sub = SubMeshes[index];
        if (hasIndexBuffer)
        {
            vulkan.Vk.CmdDrawIndexed(commandBuffer, sub.IndexCount, 1, sub.IndexStart, 0, 0);
        }
        else
        {
            vulkan.Vk.CmdDraw(commandBuffer, sub.IndexCount, 1, sub.IndexStart, 0);
        }
    }

    /// <summary>
    /// Releases the resources held by the model.
    /// </summary>
    public void Dispose()
    {
        vertexBuffer.Dispose();
        indexBuffer.Dispose();
        GC.SuppressFinalize(this);
    }
}
