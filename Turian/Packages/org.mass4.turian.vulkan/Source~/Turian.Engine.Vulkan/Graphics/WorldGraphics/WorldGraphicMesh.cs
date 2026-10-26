namespace Turian.Engine.Core;

/// <summary>Immutable graphic buffers, retired rather than overwritten while a frame can read them.</summary>
sealed class WorldGraphicMesh : IDisposable
{
    readonly Buffer vertices;
    readonly Buffer indices;
    readonly Vulkan vulkan;
    readonly uint count;

    internal WorldGraphicMesh(Vulkan vulkan, WorldGraphicGeometry geometry)
    {
        this.vulkan = vulkan;
        vertices = Upload(vulkan, geometry.Vertices, BufferUsageFlags.VertexBufferBit);
        indices = Upload(vulkan, geometry.Indices, BufferUsageFlags.IndexBufferBit);
        count = (uint)geometry.Indices.Length;
    }

    static Buffer Upload<T>(Vulkan vulkan, T[] data, BufferUsageFlags usage) where T : unmanaged
    {
        var buffer = new Buffer(vulkan, (ulong)Unsafe.SizeOf<T>(), (uint)data.Length, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = buffer.Map();
        buffer.WriteToBuffer(data);
        return buffer;
    }

    internal unsafe void Draw(CommandBuffer command)
    {
        var handle = vertices.VkBuffer;
        ulong offset = 0;
        vulkan.Vk.CmdBindVertexBuffers(command, 0, 1, &handle, &offset);
        vulkan.Vk.CmdBindIndexBuffer(command, indices.VkBuffer, 0, IndexType.Uint32);
        vulkan.Vk.CmdDrawIndexed(command, count, 1, 0, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        vertices.Dispose();
        indices.Dispose();
    }
}
