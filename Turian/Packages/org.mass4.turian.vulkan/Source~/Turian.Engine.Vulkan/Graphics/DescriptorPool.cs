namespace Turian.Engine.Core;

/// <summary>
/// Represents a descriptor pool in Vulkan, managing the allocation and deallocation of descriptor sets.
/// </summary>
public unsafe class DescriptorPool : IDisposable
{
    /// <summary>
    /// Gets the Device object, representing the GPU or similar device being used.
    /// </summary>
    public Device LveDevice { get; }

    /// <summary>
    /// Reference to Vulkan API object.
    /// </summary>
    readonly Vk vk;

    Silk.NET.Vulkan.DescriptorPool descriptorPool;

    // private readonly DescriptorPoolCreateFlags poolFlags; // = DescriptorPoolCreateFlags.None;

    /// <summary>
    /// Initializes a new instance of the DescriptorPool class.
    /// </summary>
    /// <param name="vk">The Vulkan API object.</param>
    /// <param name="device">The device object.</param>
    /// <param name="maxSets">The maximum number of descriptor sets that can be allocated from the pool.</param>
    /// <param name="poolFlags">The descriptor pool creation flags.</param>
    /// <param name="poolSizes">Array of descriptor pool sizes.</param>
    public DescriptorPool(
        Vk vk,
        Device device,
        uint maxSets,
        DescriptorPoolCreateFlags poolFlags,
        DescriptorPoolSize[] poolSizes
    )
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(poolSizes);
        this.vk = vk;
        LveDevice = device;
        // this.poolFlags = poolFlags;

        fixed (Silk.NET.Vulkan.DescriptorPool* descriptorPoolPtr = &descriptorPool)
        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            DescriptorPoolCreateInfo descriptorPoolInfo =
                new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    PoolSizeCount = (uint)poolSizes.Length,
                    PPoolSizes = poolSizesPtr,
                    MaxSets = maxSets,
                    Flags = poolFlags
                };


            var resultVulkan = vk.CreateDescriptorPool(device.VkDevice, &descriptorPoolInfo, null, descriptorPoolPtr);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create descriptor pool: {0}", resultVulkan);
            }
        }
    }

    /// <summary>
    /// Allocate a descriptor set from the pool.
    /// </summary>
    /// <param name="descriptorSetLayout">The layout of the descriptor set to allocate.</param>
    /// <param name="descriptorSet">Reference to store the allocated descriptor set.</param>
    /// <returns>True if allocation was successful, false otherwise.</returns>
    public bool AllocateDescriptorSet(
        Silk.NET.Vulkan.DescriptorSetLayout descriptorSetLayout,
        ref DescriptorSet descriptorSet
    )
    {
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            PSetLayouts = &descriptorSetLayout,
            DescriptorSetCount = 1,
        };
        var result = vk.AllocateDescriptorSets(LveDevice.VkDevice, in allocInfo, out descriptorSet);

        return result == Result.Success;
    }

    /// <summary>
    /// Releases all resources used by the DescriptorPool object.
    /// </summary>
    public void Dispose()
    {
        vk.DestroyDescriptorPool(LveDevice.VkDevice, descriptorPool, null);
        GC.SuppressFinalize(this);
    }

}
