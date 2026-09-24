namespace Turian.Engine.Core;



/// <summary>
/// Draws world-space UI panels — each a rasterized <see cref="Texture"/> on a unit quad placed by a
/// model matrix — inside the scene render pass, after the solid systems and gizmos. Depth-tested
/// and depth-writing, so a panel occludes and is occluded by scene geometry (the diegetic
/// "screen in the world"). Alpha-blended; fully transparent texels are discarded.
/// </summary>
/// <remarks>
/// The quad geometry comes from <c>gl_VertexIndex</c> (no vertex buffer). Set 0 is the scene's
/// global UBO (camera); set 1 is the panel sampler, one descriptor set cached per texture instance.
/// </remarks>
public sealed unsafe class WorldUiRenderSystem : IRenderSystem
{
    const string vertShaderPath = "worldui.vert.spv";
    const string fragShaderPath = "worldui.frag.spv";
    const string rendererName = "WorldUiRenderer";
    const int maxPanels = 32;

    readonly Vulkan vulkan;
    readonly DescriptorSetLayout panelSetLayout;
    readonly DescriptorPool pool;
    readonly Dictionary<Texture, DescriptorSet> descriptorByTexture = [];
    PipelineLayout pipelineLayout;
    StandardPipeline pipeline = null!;

    /// <summary>The panels to draw this frame. The host clears and repopulates it before each render.</summary>
    public List<WorldUiQuad> Quads { get; } = [];

    /// <summary>Creates the world-UI system for a render pass.</summary>
    /// <param name="vulkan">The shared Vulkan context.</param>
    /// <param name="renderPass">The render pass this system draws inside.</param>
    /// <param name="globalSetLayout">The scene's global descriptor set layout (set 0: camera UBO).</param>
    public WorldUiRenderSystem(Vulkan vulkan, RenderPass renderPass, DescriptorSetLayout globalSetLayout)
    {
        ArgumentNullException.ThrowIfNull(globalSetLayout);
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;

        panelSetLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .Build();

        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device)
            .SetMaxSets(maxPanels)
            .AddPoolSize(DescriptorType.CombinedImageSampler, maxPanels)
            .Build();

        CreatePipelineLayout(globalSetLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc/>
    public void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        if (Quads.Count == 0) return;

        pipeline.Bind(frameInfo.CommandBuffer);

        var globalSet = frameInfo.GlobalDescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(
            frameInfo.CommandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, in globalSet, 0, null);

        foreach (var quad in Quads)
        {
            if (quad.Texture is null) continue;

            var set = DescriptorFor(quad.Texture);
            if (set.Handle == default) continue;

            vulkan.Vk.CmdBindDescriptorSets(
                frameInfo.CommandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 1, 1, in set, 0, null);

            var model = quad.Model;
            vulkan.Vk.CmdPushConstants(
                frameInfo.CommandBuffer, pipelineLayout, ShaderStageFlags.VertexBit, 0,
                (uint)sizeof(Matrix4x4), ref model);

            vulkan.Vk.CmdDraw(frameInfo.CommandBuffer, 6, 1, 0, 0);
        }
    }

    DescriptorSet DescriptorFor(Texture texture)
    {
        if (descriptorByTexture.TryGetValue(texture, out var existing)) return existing;

        if (descriptorByTexture.Count >= maxPanels)
        {
            Log.Logger.LogWarning("WorldUiRenderSystem: more than {Max} distinct panel textures; extra panels skipped", maxPanels);
            return default;
        }

        var set = default(DescriptorSet);
        if (!pool.AllocateDescriptorSet(panelSetLayout.GetDescriptorSetLayout(), ref set))
            return default;

        var info = texture.DescriptorInfo;
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &info,
        };
        vulkan.Vk.UpdateDescriptorSets(vulkan.Device.VkDevice, 1, &write, 0, null);

        descriptorByTexture[texture] = set;
        return set;
    }

    void CreatePipelineLayout(DescriptorSetLayout globalSetLayout)
    {
        var layouts = stackalloc Silk.NET.Vulkan.DescriptorSetLayout[2];
        layouts[0] = globalSetLayout.GetDescriptorSetLayout();
        layouts[1] = panelSetLayout.GetDescriptorSetLayout();

        PushConstantRange push = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(Matrix4x4),
        };

        PipelineLayoutCreateInfo info = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push,
        };

        if (vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in info, null, out pipelineLayout) != Result.Success)
            throw new VulkanException("Vulkan: failed to create world-UI pipeline layout");
    }

    void CreatePipeline(RenderPass renderPass)
    {
        var config = new PipelineConfigInfo();
        StandardPipeline.DefaultPipelineConfigInfo(ref config);
        StandardPipeline.EnableAlphaBlending(ref config);
        StandardPipeline.EnableMultiSampling(ref config, vulkan.Device.MsaaSamples);

        config.BindingDescriptions = [];
        config.AttributeDescriptions = [];

        var depth = config.DepthStencilInfo;
        depth.DepthTestEnable = Vk.True;
        depth.DepthWriteEnable = Vk.True;
        config.DepthStencilInfo = depth;

        config.RenderPass = renderPass;
        config.PipelineLayout = pipelineLayout;

        pipeline = new StandardPipeline(vulkan.Vk, vulkan.Device, vertShaderPath, fragShaderPath, config, rendererName);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        pipeline.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        pool.Dispose();
        panelSetLayout.Dispose();
    }
}
