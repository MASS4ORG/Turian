namespace Turian.Engine.Core;

/// <summary>
/// Helper class for writing descriptor sets used for Vulkan rendering.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DescriptorSetWriter"/> class.
/// </remarks>
/// <param name="vk">The Vulkan instance.</param>
/// <param name="device">The Vulkan device.</param>
/// <param name="setLayout">The descriptor set layout to write to.</param>
public unsafe class DescriptorSetWriter(Vk vk, Device device, DescriptorSetLayout setLayout)
{
    readonly Vk vk = vk;

    readonly Device device = device;

    readonly DescriptorSetLayout setLayout = setLayout;

    readonly List<WriteDescriptorSet> writes = [];
    // Keeps DescriptorBufferInfo / DescriptorImageInfo alive (and at stable addresses) until Build runs.
    // WriteDescriptorSet stores a raw pointer to one of these — taking the address of a stack-local
    // parameter would dangle the moment WriteBuffer/WriteImage returned.
    readonly List<DescriptorBufferInfo> bufferInfos = [];
    readonly List<DescriptorImageInfo> imageInfos = [];
    // Per-write association: kind (false=buffer, true=image) and index into the matching list.
    readonly List<(bool IsImage, int Index)> infoSlots = [];

    /// <summary>
    /// Writes a buffer descriptor to the descriptor set.
    /// </summary>
    /// <param name="binding">The binding point within the descriptor set layout.</param>
    /// <param name="bufferInfo">The descriptor buffer information to write.</param>
    /// <returns>The <see cref="DescriptorSetWriter"/> instance.</returns>
    public DescriptorSetWriter WriteBuffer(uint binding, DescriptorBufferInfo bufferInfo)
    {
        ValidateBinding(binding);

        var bindingDescription = setLayout.Bindings[binding];
        ValidateDescriptorCount(bindingDescription);

        infoSlots.Add((false, bufferInfos.Count));
        bufferInfos.Add(bufferInfo);
        writes.Add(
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = bindingDescription.DescriptorType,
                DstBinding = binding,
                DescriptorCount = 1,
            }
        );

        return this;
    }

    /// <summary>
    /// Writes an image descriptor to the descriptor set.
    /// </summary>
    /// <param name="binding">The binding point within the descriptor set layout.</param>
    /// <param name="imageInfo">The descriptor image information to write.</param>
    /// <returns>The <see cref="DescriptorSetWriter"/> instance.</returns>
    public DescriptorSetWriter WriteImage(uint binding, DescriptorImageInfo imageInfo)
    {
        ValidateBinding(binding);

        var bindingDescription = setLayout.Bindings[binding];
        ValidateDescriptorCount(bindingDescription);

        infoSlots.Add((true, imageInfos.Count));
        imageInfos.Add(imageInfo);
        writes.Add(
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = bindingDescription.DescriptorType,
                DstBinding = binding,
                DescriptorCount = 1,
            }
        );

        return this;
    }

    /// <summary>
    /// Builds and updates a descriptor set.
    /// </summary>
    /// <param name="pool">The descriptor pool used for allocation.</param>
    /// <param name="layout">The descriptor set layout.</param>
    /// <param name="set">The descriptor set to update and allocate.</param>
    /// <returns>True if the descriptor set was successfully updated and allocated, otherwise false.</returns>
    public bool Build(
        DescriptorPool pool,
        Silk.NET.Vulkan.DescriptorSetLayout layout,
        ref DescriptorSet set
    )
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (!pool.AllocateDescriptorSet(setLayout.GetDescriptorSetLayout(), ref set))
        {
            return false;
        }

        Overwrite(ref set);
        return true;
    }

    void Overwrite(ref DescriptorSet set)
    {
        var writesArray = writes.ToArray();
        var bufferArray = bufferInfos.ToArray();
        var imageArray = imageInfos.ToArray();

        fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
        fixed (DescriptorImageInfo* imagePtr = imageArray)
        fixed (WriteDescriptorSet* writesPtr = writesArray)
        {
            for (var i = 0; i < writesArray.Length; i++)
            {
                writesPtr[i].DstSet = set;
                var slot = infoSlots[i];
                if (slot.IsImage)
                    writesPtr[i].PImageInfo = &imagePtr[slot.Index];
                else
                    writesPtr[i].PBufferInfo = &bufferPtr[slot.Index];
            }

            vk.UpdateDescriptorSets(device.VkDevice, (uint)writesArray.Length, writesPtr, 0, null);
        }
    }

    void ValidateBinding(uint binding)
    {
        if (!setLayout.Bindings.ContainsKey(binding))
        {
            throw new KeyNotFoundException(
                "Layout does not contain the specified binding at {binding}"
            );
        }
    }

    static void ValidateDescriptorCount(DescriptorSetLayoutBinding bindingDescription)
    {
        if (bindingDescription.DescriptorCount > 1)
        {
            throw new KeyNotFoundException(
                "Binding single descriptor info, but binding expects multiple"
            );
        }
    }
}
