namespace Turian.Engine.Core;

/// <summary>Owns a graphic's sampler descriptor and optionally its text distance-field image.</summary>
sealed class WorldGraphicBinding : IDisposable
{
    readonly DescriptorPool pool;
    readonly Texture? ownedTexture;

    internal DescriptorSet Set { get; }
    internal Texture Texture { get; }

    internal unsafe WorldGraphicBinding(Vulkan vulkan, DescriptorSetLayout layout, Texture texture, bool ownsTexture)
    {
        Texture = texture;
        ownedTexture = ownsTexture ? texture : null;
        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device).SetMaxSets(1)
            .AddPoolSize(DescriptorType.CombinedImageSampler, 1).Build();
        var set = default(DescriptorSet);
        if (!pool.AllocateDescriptorSet(layout.GetDescriptorSetLayout(), ref set))
        {
            throw new VulkanException("Vulkan: failed to allocate graphic sampler descriptor");
        }
        var info = texture.DescriptorInfo;
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &info,
        };
        vulkan.Vk.UpdateDescriptorSets(vulkan.Device.VkDevice, 1, &write, 0, null);
        Set = set;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        pool.Dispose();
        ownedTexture?.Dispose();
    }
}
