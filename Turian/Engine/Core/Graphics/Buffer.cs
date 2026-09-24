namespace Turian.Engine.Core;

/// <summary>
/// Represents a Vulkan buffer object.
/// </summary>
[PublicAPI]
public unsafe class Buffer : IDisposable
{
    /// <summary>
    /// Gets the size of the buffer.
    /// </summary>
    public ulong BufferSize { get; }

    /// <summary>
    /// Gets the number of instances of the buffer.
    /// </summary>
    public uint InstanceCount { get; }

    /// <summary>
    /// Gets or sets the size of each instance in the buffer.
    /// </summary>
    public ulong InstanceSize { get; set; }

    /// <summary>
    /// Gets the alignment size for the buffer.
    /// </summary>
    public ulong AlignmentSize { get; }

    /// <summary>
    /// Gets the usage flags for the buffer.
    /// </summary>
    public BufferUsageFlags UsageFlags { get; }

    /// <summary>
    /// Gets the memory property flags for the buffer.
    /// </summary>
    public MemoryPropertyFlags MemoryPropertyFlags { get; }

    /// <summary>
    /// Buffer
    /// </summary>
    public Silk.NET.Vulkan.Buffer VkBuffer => buffer;

    readonly Vulkan vulkan;
    void* mapped = null;
    Silk.NET.Vulkan.Buffer buffer;
    DeviceMemory memory;

    /// <summary>
    /// Creates a new Vulkan buffer.
    /// </summary>
    /// <param name="vulkan">The Vulkan context.</param>
    /// <param name="instanceSize">The size of each instance in the buffer.</param>
    /// <param name="instanceCount">The number of instances in the buffer.</param>
    /// <param name="usageFlags">The usage flags for the buffer.</param>
    /// <param name="memoryPropertyFlags">The memory property flags for the buffer.</param>
    /// <param name="minOffsetAlignment">The minimum offset alignment for the buffer.</param>
    public Buffer(
        Vulkan vulkan,
        ulong instanceSize,
        uint instanceCount,
        BufferUsageFlags usageFlags,
        MemoryPropertyFlags memoryPropertyFlags,
        ulong minOffsetAlignment = 1
    )
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;
        InstanceCount = instanceCount;
        InstanceSize = instanceSize;
        UsageFlags = usageFlags;
        MemoryPropertyFlags = memoryPropertyFlags;

        AlignmentSize = GetAlignment(instanceSize, minOffsetAlignment);
        BufferSize = AlignmentSize * instanceCount;
        vulkan.Device.CreateBuffer(
            BufferSize,
            usageFlags,
            memoryPropertyFlags,
            ref buffer,
            ref memory
        );
    }

    /// <summary>
    /// Maps a memory range of this buffer.
    /// </summary>
    /// <param name="size">The size of the memory range to map.</param>
    /// <param name="offset">The byte offset from the beginning.</param>
    /// <returns>The result of the buffer mapping call.</returns>
    public Result Map(ulong size = Vk.WholeSize, ulong offset = 0)
    {
        Debug.Assert(
            buffer.Handle != 0 && memory.Handle != 0,
            "Called map on buffer before create"
        );
        return vulkan.Vk.MapMemory(vulkan.Device.VkDevice, memory, offset, size, 0, ref mapped);
    }

    /// <summary>
    /// Unmaps a previously mapped memory range.
    /// </summary>
    public void UnMap()
    {
        if (mapped is not null)
        {
            vulkan.Vk.UnmapMemory(vulkan.Device.VkDevice, memory);
            mapped = null;
        }
    }

    /// <summary>
    /// Reads data from the mapped buffer into a specified type.
    /// </summary>
    /// <typeparam name="T">The type of data to read.</typeparam>
    /// <returns>The data read from the buffer.</returns>
    public T ReadFromBuffer<T>()
        where T : unmanaged
    {
        var data2 = new T[1];
        fixed (void* dataPtr = data2)
        {
            var dataSize = sizeof(T) * data2.Length;
            System.Buffer.MemoryCopy(mapped, dataPtr, dataSize, dataSize);
        }

        return data2[0];
    }

    /// <summary>
    /// Copies the specified data to the mapped buffer. Default value writes whole buffer range
    /// </summary>
    /// <typeparam name="T">The type of data to write.</typeparam>
    /// <param name="data">Pointer to the data to copy.</param>
    /// <param name="size">Size of the data to copy. Pass VK_WHOLE_SIZE to flush the complete buffer range.</param>
    /// <param name="offset">Byte offset from beginning of mapped region.</param>
    public void WriteToBuffer<T>(T[] data, ulong size = Vk.WholeSize, ulong offset = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(data);
        if (size == Vk.WholeSize)
        {
            fixed (void* dataPtr = data)
            {
                var dataSize = sizeof(T) * data.Length;
                System.Buffer.MemoryCopy(dataPtr, mapped, dataSize, dataSize);
            }
        }
        else
        {
            // https://github.com/dotnet/runtime/discussions/73108
            // You can just do span1.CopyTo(span2.Slice(index)) or span1.CopyTo[span2[index..]]. No more overload is needed.
            // var tmpSpan = new Span<T>(mapped, (int)instanceCount);
            // data.AsSpan().CopyTo(tmpSpan[(int)offset..]);

            throw new NotImplementedException("don't have offset stuff working yet");
        }
    }

    /// <summary>
    /// Writes data to the mapped buffer.
    /// </summary>
    /// <typeparam name="T">The type of data to write.</typeparam>
    /// <param name="data">The data to write.</param>
    /// <param name="size">The size of the data to copy.</param>
    /// <param name="offset">The byte offset from the beginning of the mapped region.</param>
    public void WriteToBuffer<T>(T data, ulong size = Vk.WholeSize, ulong offset = 0)
        where T : unmanaged
    {
        if (size == Vk.WholeSize)
        {
            T[] data2 = [data];
            fixed (void* dataPtr = data2)
            {
                var dataSize = sizeof(T) * data2.Length;
                System.Buffer.MemoryCopy(dataPtr, mapped, dataSize, dataSize);
            }
        }
        else
        {
            // https://github.com/dotnet/runtime/discussions/73108
            // You can just do span1.CopyTo(span2.Slice(index)) or span1.CopyTo[span2[index..]]. No more overload is needed.
            // var tmpSpan = new Span<T>(mapped, (int)instanceCount);
            // data.AsSpan().CopyTo(tmpSpan[(int)offset..]);

            throw new NotImplementedException("don't have offset stuff working yet");
        }
    }

    /// <summary>
    /// Writes bytes to the mapped buffer.
    /// </summary>
    /// <param name="data">The byte data to write.</param>
    public void WriteBytesToBuffer(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        fixed (void* dataPtr = data)
        {
            var dataSize = sizeof(byte) * data.Length;
            System.Buffer.MemoryCopy(dataPtr, mapped, dataSize, dataSize);
        }
    }

    /// <summary>
    /// Writes data from the given array to the Vulkan buffer, starting at the specified index.
    /// </summary>
    /// <typeparam name="T">The type of data to write.</typeparam>
    /// <param name="data">The source array containing the data to be written.</param>
    /// <param name="index">The index in the target buffer where writing should begin.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    public void WriteToIndex<T>(T[] data, int index)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tmpSpan = new Span<T>(mapped, data.Length);
        data.AsSpan().CopyTo(tmpSpan[index..]);
    }

    /// <summary>
    /// Flush a memory range of the buffer to make it visible to the device.
    /// </summary>
    /// <remarks>
    /// Only required for non-coherent memory.
    /// </remarks>
    /// <param name="size">(Optional) Size of the memory range to flush. Pass VK_WHOLE_SIZE to flush the complete buffer range.</param>
    /// <param name="offset">(Optional) Byte offset from the beginning.</param>
    /// <returns>VkResult of the flush call.</returns>
    public Result Flush(ulong size = Vk.WholeSize, ulong offset = 0)
    {
        MappedMemoryRange mappedRange = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = offset,
            Size = size
        };
        return vulkan.Vk.FlushMappedMemoryRanges(vulkan.Device.VkDevice, 1, in mappedRange);
    }

    /// <summary>
    /// Invalidate a memory range of the buffer to make it visible to the host.
    /// </summary>
    /// <remarks>
    /// Only required for non-coherent memory.
    /// </remarks>
    /// <param name="size">(Optional) Size of the memory range to invalidate. Pass VK_WHOLE_SIZE to invalidate the complete buffer range.</param>
    /// <param name="offset">(Optional) Byte offset from the beginning.</param>
    /// <returns>VkResult of the invalidate call.</returns>
    public Result Invalidate(ulong size = Vk.WholeSize, ulong offset = 0)
    {
        MappedMemoryRange mappedRange = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = offset,
            Size = size
        };
        return vulkan.Vk.InvalidateMappedMemoryRanges(vulkan.Device.VkDevice, 1, in mappedRange);
    }

    /// <summary>
    /// Create a buffer info descriptor.
    /// </summary>
    /// <param name="size">(Optional) Size of the memory range of the descriptor.</param>
    /// <param name="offset">(Optional) Byte offset from the beginning.</param>
    /// <returns>VkDescriptorBufferInfo of specified offset and range.</returns>
    public DescriptorBufferInfo DescriptorInfo(ulong size = Vk.WholeSize, ulong offset = 0)
    {
        return new()
        {
            Buffer = buffer,
            Offset = offset,
            Range = size
        };
    }

    /// <summary>
    /// Flush the memory range at index * alignmentSize of the buffer to make it visible to the device.
    /// </summary>
    /// <param name="index">Used in offset calculation.</param>
    /// <returns>VkResult of the flush call.</returns>
    public Result FlushIndex(int index)
    {
        return Flush(AlignmentSize, (ulong)index * AlignmentSize);
    }

    /// <summary>
    /// Create a buffer info descriptor.
    /// </summary>
    /// <param name="index">Specifies the region given by index * alignmentSize.</param>
    /// <returns>VkDescriptorBufferInfo for the instance at index.</returns>
    public DescriptorBufferInfo DescriptorInfoForIndex(int index)
    {
        return DescriptorInfo(AlignmentSize, (ulong)index * AlignmentSize);
    }

    /// <summary>
    /// Invalidate a memory range of the buffer to make it visible to the host.
    /// </summary>
    /// <remarks>
    /// Only required for non-coherent memory.
    /// </remarks>
    /// <param name="index">Specifies the region to invalidate: index * alignmentSize.</param>
    /// <returns>VkResult of the invalidate call.</returns>
    public Result InvalidateIndex(int index)
    {
        return Invalidate(AlignmentSize, (ulong)index * AlignmentSize);
    }

    /// <summary>
    /// Disposes of the buffer and associated resources.
    /// </summary>
    public void Dispose()
    {
        UnMap();
        vulkan.Vk.DestroyBuffer(vulkan.Device.VkDevice, buffer, null);
        vulkan.Vk.FreeMemory(vulkan.Device.VkDevice, memory, null);
        GC.SuppressFinalize(this);
    }

    static ulong GetAlignment(ulong instanceSize, ulong minOffsetAlignment)
    {
        if (minOffsetAlignment > 0)
        {
            return (instanceSize + minOffsetAlignment - 1) & ~(minOffsetAlignment - 1);
        }

        return instanceSize;
    }
}
