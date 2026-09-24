namespace Turian.Engine.Core;

/// <summary>
/// Per-material GPU resource: a descriptor set bound at set=1 holding the PBR factor
/// UBO and the five PBR texture samplers. Built once per <see cref="MaterialAsset"/>
/// from a fully-hydrated source asset; cached on <see cref="MaterialAsset.GetContent"/>.
/// </summary>
public sealed class MaterialResource : IDisposable
{
    readonly Buffer ubo;
    DescriptorSet descriptorSet;

    /// <summary>The descriptor set bound at set=1 during a draw using this material.</summary>
    public DescriptorSet DescriptorSet => descriptorSet;

    /// <summary>
    /// Builds a descriptor set for the given material. Texture references resolve through
    /// <see cref="TextureAsset.GetContent"/>; empty or unresolved slots bind the matching
    /// <see cref="DefaultTextures"/> entry.
    /// </summary>
    public MaterialResource(MaterialDescriptorContext context, MaterialAsset material)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(material);

        // The normal map's convention travels on the texture asset, not the material, so it has to
        // be resolved before the UBO is written.
        var normalAsset = material.NormalTexture?.Resolve(AssetDatabase.Instance);

        var uboData = new MaterialPbrUbo
        {
            BaseColorFactor = material.BaseColorFactor,
            EmissiveFactor = new Vector4(material.EmissiveFactor.X, material.EmissiveFactor.Y, material.EmissiveFactor.Z, 0f),
            MetallicFactor = material.MetallicFactor,
            RoughnessFactor = material.RoughnessFactor,
            OcclusionStrength = 1f,
            FlipGreenChannel = normalAsset?.FlipGreenChannel == true ? 1f : 0f,
        };

        ubo = new Buffer(
            context.Vulkan,
            instanceSize: (ulong)Unsafe.SizeOf<MaterialPbrUbo>(),
            instanceCount: 1,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = ubo.Map();
        ubo.WriteToBuffer(uboData);

        var baseColor = ResolveTexture(context, material.BaseColorTexture, context.Defaults.White);
        var metallicRoughness = ResolveTexture(
            context,
            material.MetallicRoughnessTexture,
            context.Defaults.LinearWhite,
            unresolved: context.Defaults.Dielectric);
        var normal = normalAsset?.GetContent(context.Vulkan) ?? context.Defaults.FlatNormal;
        var occlusion = ResolveTexture(context, material.OcclusionTexture, context.Defaults.LinearWhite);
        var emissive = ResolveTexture(context, material.EmissiveTexture, context.Defaults.Black);

        var writer = new DescriptorSetWriter(context.Vulkan.Vk, context.Vulkan.Device, context.SetLayout)
            .WriteBuffer(MaterialDescriptorContext.UboBinding, ubo.DescriptorInfo())
            .WriteImage(MaterialDescriptorContext.BaseColorBinding, baseColor.DescriptorInfo)
            .WriteImage(MaterialDescriptorContext.MetallicRoughnessBinding, metallicRoughness.DescriptorInfo)
            .WriteImage(MaterialDescriptorContext.NormalBinding, normal.DescriptorInfo)
            .WriteImage(MaterialDescriptorContext.OcclusionBinding, occlusion.DescriptorInfo)
            .WriteImage(MaterialDescriptorContext.EmissiveBinding, emissive.DescriptorInfo);

        if (!context.AllocateSet(writer, ref descriptorSet))
            throw new VulkanException("Vulkan: failed to allocate per-material descriptor set");
    }

    /// <summary>
    /// Resolves a texture slot to an uploaded texture.
    /// </summary>
    /// <param name="context">The context holding the fallback textures.</param>
    /// <param name="reference">The material's reference for this slot.</param>
    /// <param name="fallback">Texture bound when the slot declares no reference.</param>
    /// <param name="unresolved">
    /// Texture bound when the slot declares a reference that could not be read.
    /// Defaults to <paramref name="fallback"/>.
    /// </param>
    static Texture ResolveTexture(
        MaterialDescriptorContext context,
        AssetReference<TextureAsset>? reference,
        Texture fallback,
        Texture? unresolved = null)
    {
        if (reference is null || reference.IsEmpty)
        {
            return fallback;
        }

        var resolved = reference.Resolve(AssetDatabase.Instance);
        return resolved?.GetContent(context.Vulkan) ?? unresolved ?? fallback;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ubo.Dispose();
    }
}
