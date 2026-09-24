namespace Turian.Engine.Core;

/// <summary>
/// A builder class for constructing a descriptor set layout in Vulkan.
/// </summary>
/// <remarks>
/// Initializes a new instance of the DescriptorSetLayoutBuilder class.
/// </remarks>
/// <param name="vk">The Vulkan API object.</param>
/// <param name="device">The device object.</param>
public class DescriptorSetLayoutBuilder(Vk vk, Device device)
{
    readonly Vk vk = vk;
    readonly Device device = device;
    readonly Dictionary<uint, DescriptorSetLayoutBinding> bindings = [];

    /// <summary>
    /// Adds a binding to the descriptor set layout builder.
    /// </summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="descriptorType">The type of descriptor.</param>
    /// <param name="stageFlags">Shader stages that can access this binding.</param>
    /// <param name="count">The number of descriptors in this binding.</param>
    /// <returns>Returns the updated DescriptorSetLayoutBuilder.</returns>
    public DescriptorSetLayoutBuilder AddBinding(
        uint binding,
        DescriptorType descriptorType,
        ShaderStageFlags stageFlags,
        uint count = 1
    )
    {
        if (bindings.ContainsKey(binding))
        {
            throw new ArgumentException("Binding {binding} is already in use, can't add");
        }
        DescriptorSetLayoutBinding layoutBinding =
            new()
            {
                Binding = binding,
                DescriptorType = descriptorType,
                DescriptorCount = count,
                StageFlags = stageFlags
            };
        bindings[binding] = layoutBinding;
        return this;
    }

    /// <summary>
    /// Adds a binding with an immutable sampler to the descriptor set layout builder.
    /// </summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="descriptorType">The type of descriptor.</param>
    /// <param name="stageFlags">Shader stages that can access this binding.</param>
    /// <param name="sampler">The immutable sampler for the binding.</param>
    /// <param name="count">The number of descriptors in this binding.</param>
    /// <returns>Returns the updated DescriptorSetLayoutBuilder.</returns>
    public unsafe DescriptorSetLayoutBuilder AddBinding(
        uint binding,
        DescriptorType descriptorType,
        ShaderStageFlags stageFlags,
        Sampler sampler,
        uint count = 1
    )
    {
        if (bindings.ContainsKey(binding))
        {
            throw new ArgumentException("Binding {binding} is already in use, can't add");
        }
        DescriptorSetLayoutBinding layoutBinding =
            new()
            {
                Binding = binding,
                DescriptorType = descriptorType,
                DescriptorCount = count,
                StageFlags = stageFlags,
                PImmutableSamplers = (Sampler*)Unsafe.AsPointer(ref sampler)
            };
        bindings[binding] = layoutBinding;
        return this;
    }

    /// <summary>
    /// Builds the descriptor set layout.
    /// </summary>
    /// <returns>The constructed DescriptorSetLayout object.</returns>
    public DescriptorSetLayout Build()
    {
        return new DescriptorSetLayout(vk, device, bindings);
    }
}
