namespace Turian.Engine.Core;

/// <summary>
/// A helper builder class for chaining calls to configure and create a <see cref="DescriptorPool"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DescriptorPoolBuilder"/> class.
/// </remarks>
/// <param name="vk">The Vulkan API object.</param>
/// <param name="device">The device object.</param>
public class DescriptorPoolBuilder(Vk vk, Device device)
{
    readonly Vk vk = vk;
    readonly Device device = device;

    readonly List<DescriptorPoolSize> poolSizes = [];
    DescriptorPoolCreateFlags poolFlags;
    uint maxSets;

    /// <summary>
    /// Adds a descriptor pool size to the builder.
    /// </summary>
    /// <param name="descriptorType">The type of descriptor.</param>
    /// <param name="count">The number of descriptors of this type to allocate.</param>
    /// <returns>The builder, with the pool size added.</returns>
    public DescriptorPoolBuilder AddPoolSize(DescriptorType descriptorType, uint count)
    {
        poolSizes.Add(new DescriptorPoolSize(descriptorType, count));
        return this;
    }

    /// <summary>
    /// Sets the pool flags for the descriptor pool.
    /// </summary>
    /// <param name="flags">The descriptor pool create flags.</param>
    /// <returns>The builder, with the pool flags set.</returns>
    public DescriptorPoolBuilder SetPoolFlags(DescriptorPoolCreateFlags flags)
    {
        poolFlags = flags;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of descriptor sets that can be allocated from the descriptor pool.
    /// </summary>
    /// <param name="count">The maximum number of descriptor sets.</param>
    /// <returns>The builder, with the max sets set.</returns>
    public DescriptorPoolBuilder SetMaxSets(uint count)
    {
        maxSets = count;
        return this;
    }

    /// <summary>
    /// Builds and returns a new instance of <see cref="DescriptorPool"/>.
    /// </summary>
    /// <returns>The newly created <see cref="DescriptorPool"/> instance.</returns>
    public DescriptorPool Build()
    {
        return new DescriptorPool(vk, device, maxSets, poolFlags, [.. poolSizes]);
    }
}
