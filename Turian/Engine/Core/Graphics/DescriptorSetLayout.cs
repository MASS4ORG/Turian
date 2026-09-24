namespace Turian.Engine.Core;

/// <summary>
/// Represents a descriptor set layout in Vulkan, specifying the types and configurations of descriptors.
/// </summary>
public unsafe class DescriptorSetLayout : IDisposable
{
    /// <summary>
    /// Gets the Vulkan descriptor set layout object associated with this descriptor set layout.
    /// </summary>
    /// <returns>The Vulkan descriptor set layout.</returns>
    public Silk.NET.Vulkan.DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    /// <summary>
    /// Gets the dictionary of descriptor set layout bindings used in this descriptor set layout.
    /// </summary>
    public Dictionary<uint, DescriptorSetLayoutBinding> Bindings { get; }

    readonly Vk vk;
    readonly Device device;
    Silk.NET.Vulkan.DescriptorSetLayout descriptorSetLayout;

    /// <summary>
    /// Initializes a new instance of the <see cref="DescriptorSetLayout"/> class.
    /// </summary>
    /// <param name="vk">The Vulkan instance.</param>
    /// <param name="device">The Vulkan device.</param>
    /// <param name="bindings">A dictionary of descriptor set layout bindings.</param>
    public DescriptorSetLayout(
        Vk vk,
        Device device,
        Dictionary<uint, DescriptorSetLayoutBinding> bindings
    )
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(device);
        this.vk = vk;
        this.device = device;
        Bindings = bindings;

        fixed (DescriptorSetLayoutBinding* setLayoutPtr = Bindings.Values.ToArray())
        {
            DescriptorSetLayoutCreateInfo descriptorSetLayoutInfo =
                new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)Bindings.Count,
                    PBindings = setLayoutPtr
                };

            var resultVulkan = vk.CreateDescriptorSetLayout(device.VkDevice, &descriptorSetLayoutInfo, null, out descriptorSetLayout);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create descriptor set layout: {0}", resultVulkan);
            }
        }
    }

    /// <summary>
    /// Releases the resources used by this descriptor set layout.
    /// </summary>
    public void Dispose()
    {
        vk.DestroyDescriptorSetLayout(device.VkDevice, descriptorSetLayout, null);
        GC.SuppressFinalize(this);
    }
}
