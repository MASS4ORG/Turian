namespace Turian.Engine.Core;

/// <summary>
/// Holds the per-material descriptor set layout, allocation pool, and 1×1 fallback
/// textures used by every <see cref="MaterialResource"/>. Constructed and owned by
/// the render system; passed to <see cref="MaterialAsset.GetContent"/> when a draw
/// requests a material.
/// </summary>
public sealed class MaterialDescriptorContext : IDisposable
{
    /// <summary>set=1, binding=0: per-material UBO (factors).</summary>
    public const uint UboBinding = 0;
    /// <summary>set=1, binding=1: combined image sampler — base color (sRGB).</summary>
    public const uint BaseColorBinding = 1;
    /// <summary>set=1, binding=2: combined image sampler — metallic-roughness (linear; B=metallic, G=roughness).</summary>
    public const uint MetallicRoughnessBinding = 2;
    /// <summary>set=1, binding=3: combined image sampler — tangent-space normal map (linear).</summary>
    public const uint NormalBinding = 3;
    /// <summary>set=1, binding=4: combined image sampler — ambient occlusion (linear, R channel).</summary>
    public const uint OcclusionBinding = 4;
    /// <summary>set=1, binding=5: combined image sampler — emissive (sRGB).</summary>
    public const uint EmissiveBinding = 5;

    const uint materialSlotCount = 5;
    const uint maxMaterialsPerPool = 256;

    /// <summary>The Vulkan context used by all material resources built through this context.</summary>
    public Vulkan Vulkan { get; }

    /// <summary>The descriptor set layout shared by every PBR material.</summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>Fallback 1×1 textures bound to material slots that have no glTF reference.</summary>
    public DefaultTextures Defaults { get; }

    readonly List<DescriptorPool> pools = [];
    readonly uint materialsPerPool;
    MaterialResource? defaultResource;

    /// <summary>
    /// Material resource the renderer binds for submeshes with no <c>MaterialIndex</c>:
    /// white base color, rough, non-metallic, no emissive. Geometry without an
    /// authored material draws through the PBR pipeline with it.
    /// </summary>
    public MaterialResource DefaultMaterial =>
        defaultResource ??= new MaterialResource(
            this,
            new MaterialAsset { MetallicFactor = 0f, RoughnessFactor = 0.8f });

    /// <summary>
    /// Creates the shared layout, first pool and fallback textures.
    /// </summary>
    /// <param name="vulkan">The Vulkan context every material resource is built on.</param>
    /// <param name="materialsPerPool">
    /// How many <see cref="MaterialResource"/> instances one descriptor pool holds. This is a
    /// growth increment, not a cap — pools are added as needed.
    /// </param>
    public MaterialDescriptorContext(Vulkan vulkan, uint materialsPerPool = maxMaterialsPerPool)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        Vulkan = vulkan;
        this.materialsPerPool = Math.Max(1u, materialsPerPool);

        SetLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(UboBinding, DescriptorType.UniformBuffer, ShaderStageFlags.FragmentBit)
            .AddBinding(BaseColorBinding, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .AddBinding(MetallicRoughnessBinding, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .AddBinding(NormalBinding, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .AddBinding(OcclusionBinding, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .AddBinding(EmissiveBinding, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .Build();

        pools.Add(CreatePool());

        Defaults = new DefaultTextures(vulkan);
    }

    /// <summary>
    /// Allocates and writes a per-material descriptor set, adding a pool when the current one is
    /// full. A fixed cap would fail on any scene with more materials than it allowed — Bistro alone
    /// has 277.
    /// </summary>
    /// <param name="writer">The writer holding the material's buffer and image bindings.</param>
    /// <param name="set">Receives the allocated set.</param>
    /// <returns><c>true</c> when the set was allocated and written.</returns>
    public bool AllocateSet(DescriptorSetWriter writer, ref DescriptorSet set)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var layout = SetLayout.GetDescriptorSetLayout();
        if (writer.Build(pools[^1], layout, ref set))
        {
            return true;
        }

        // Vulkan may report either OutOfPoolMemory or FragmentedPool; both mean "ask a fresh pool".
        pools.Add(CreatePool());
        return writer.Build(pools[^1], layout, ref set);
    }

    DescriptorPool CreatePool() =>
        new DescriptorPoolBuilder(Vulkan.Vk, Vulkan.Device)
            .SetMaxSets(materialsPerPool)
            .AddPoolSize(DescriptorType.UniformBuffer, materialsPerPool)
            .AddPoolSize(DescriptorType.CombinedImageSampler, materialsPerPool * materialSlotCount)
            .Build();

    /// <inheritdoc />
    public void Dispose()
    {
        // Cached resources hold descriptor sets from the pools below; they have to go first.
        MaterialAsset.EvictResourcesFor(this);

        defaultResource?.Dispose();
        Defaults.Dispose();
        foreach (var pool in pools)
        {
            pool.Dispose();
        }

        pools.Clear();
        SetLayout.Dispose();
    }
}
