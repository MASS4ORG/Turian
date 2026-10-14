namespace Turian.Engine.Core;

/// <summary>A storage buffer of <see cref="InstanceData"/> and the set-2 descriptor that binds it.</summary>
sealed class InstanceBuffer : IDisposable
{
    const int minimumCapacity = 64;
    readonly DescriptorPool pool;

    /// <summary>The GPU buffer: host-visible and mapped, or device-local and filled by transfer.</summary>
    public Buffer Buffer { get; }

    /// <summary>The descriptor set binding <see cref="Buffer"/> at set 2.</summary>
    public DescriptorSet Set { get; }

    /// <summary>The number of instances the buffer holds.</summary>
    public int Capacity { get; }

    /// <summary>Allocates the buffer and writes its descriptor.</summary>
    /// <param name="vulkan">The Vulkan context.</param>
    /// <param name="layout">The set-2 layout.</param>
    /// <param name="capacity">The number of instances to hold.</param>
    /// <param name="hostVisible">Whether the CPU writes the buffer directly instead of through a transfer.</param>
    public InstanceBuffer(Vulkan vulkan, DescriptorSetLayout layout, int capacity, bool hostVisible)
    {
        Capacity = capacity;
        Buffer = hostVisible
            ? new Buffer(vulkan, InstanceData.Size, (uint)capacity, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
            : new Buffer(vulkan, InstanceData.Size, (uint)capacity,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        if (hostVisible && Buffer.Map() != Result.Success)
        {
            throw new VulkanException("Instance buffer mapping failed.");
        }

        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device).SetMaxSets(1)
            .AddPoolSize(DescriptorType.StorageBuffer, 1).Build();
        DescriptorSet set = default;
        if (!new DescriptorSetWriter(vulkan.Vk, vulkan.Device, layout).WriteBuffer(0, Buffer.DescriptorInfo())
                .Build(pool, layout.GetDescriptorSetLayout(), ref set))
        {
            throw new VulkanException("Instance descriptor allocation failed.");
        }

        Set = set;
    }

    /// <summary>The mapped instances of a host-visible buffer.</summary>
    public Span<InstanceData> Instances => Buffer.AsSpan<InstanceData>();

    /// <summary>The capacity to allocate for <paramref name="required"/> instances: doubled until it fits.</summary>
    /// <param name="current">The current capacity, or zero.</param>
    /// <param name="required">The instances that must fit.</param>
    public static int Grow(int current, int required)
    {
        var capacity = Math.Max(current, minimumCapacity);
        while (capacity < required)
        {
            capacity *= 2;
        }

        return capacity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        pool.Dispose();
        Buffer.Dispose();
    }
}
