namespace Turian.Engine.Core;

/// <summary>
/// Composites a single texture over the frame with a fullscreen triangle and straight-alpha
/// blending, inside the same render pass as the scene and after every solid and gizmo system.
/// The engine's screen-space GUI feeds it the rasterized UI each frame; it is otherwise a
/// generic "draw this texture on top" pass.
/// </summary>
/// <remarks>
/// <see cref="Source"/> is expected to change rarely — a resize hands over a new texture
/// instance and callers recreate this system on resize anyway — so the descriptor set is
/// rewritten in place (behind a device-idle wait) only when the instance actually changes.
/// </remarks>
public sealed unsafe class OverlayRenderSystem : IRenderSystem
{
    const string vertShaderPath = "overlay.vert.spv";
    const string fragShaderPath = "overlay.frag.spv";
    const string rendererName = "OverlayRenderer";

    readonly Vulkan vulkan;

    DescriptorSetLayout setLayout;
    DescriptorPool pool;
    DescriptorSet descriptorSet;
    PipelineLayout pipelineLayout;
    StandardPipeline? pipeline;

    Texture? source;
    bool descriptorDirty;

    /// <summary>
    /// Creates the overlay system for a render pass. The pipeline's sample count is matched to
    /// the device MSAA level, so it is compatible with the scene's MSAA render pass.
    /// </summary>
    /// <param name="vulkan">The shared Vulkan context.</param>
    /// <param name="renderPass">The render pass this system draws inside.</param>
    public OverlayRenderSystem(Vulkan vulkan, RenderPass renderPass)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;

        setLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
            .Build();

        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device)
            .SetMaxSets(1)
            .AddPoolSize(DescriptorType.CombinedImageSampler, 1)
            .Build();

        if (!pool.AllocateDescriptorSet(setLayout.GetDescriptorSetLayout(), ref descriptorSet))
            throw new VulkanException("Vulkan: failed to allocate overlay descriptor set");

        CreatePipelineLayout();
        CreatePipeline(renderPass);
    }

    /// <summary>
    /// The texture to composite, or <c>null</c> to draw nothing. Setting a different instance
    /// schedules a descriptor rewrite on the next <see cref="Render"/>.
    /// </summary>
    public Texture? Source
    {
        get => source;
        set
        {
            if (ReferenceEquals(source, value)) return;
            source = value;
            descriptorDirty = value is not null;
        }
    }

    /// <inheritdoc/>
    public void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        var tex = source;
        if (tex is null) return;

        if (descriptorDirty)
        {
            _ = vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            WriteDescriptor(tex);
            descriptorDirty = false;
        }

        pipeline!.Bind(frameInfo.CommandBuffer);

        var set = descriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(
            frameInfo.CommandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, in set, 0, null);

        vulkan.Vk.CmdDraw(frameInfo.CommandBuffer, 3, 1, 0, 0);
    }

    void WriteDescriptor(Texture tex)
    {
        var info = tex.DescriptorInfo;
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &info,
        };
        vulkan.Vk.UpdateDescriptorSets(vulkan.Device.VkDevice, 1, &write, 0, null);
    }

    void CreatePipelineLayout()
    {
        var layouts = stackalloc Silk.NET.Vulkan.DescriptorSetLayout[1];
        layouts[0] = setLayout.GetDescriptorSetLayout();

        PipelineLayoutCreateInfo info = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = layouts,
            PushConstantRangeCount = 0,
        };

        if (vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in info, null, out pipelineLayout) != Result.Success)
            throw new VulkanException("Vulkan: failed to create overlay pipeline layout");
    }

    void CreatePipeline(RenderPass renderPass)
    {
        var config = new PipelineConfigInfo();
        StandardPipeline.DefaultPipelineConfigInfo(ref config);
        StandardPipeline.EnableAlphaBlending(ref config);
        StandardPipeline.EnableMultiSampling(ref config, vulkan.Device.MsaaSamples);

        // No vertex buffer: the triangle comes from gl_VertexIndex.
        config.BindingDescriptions = [];
        config.AttributeDescriptions = [];

        var depthStencil = config.DepthStencilInfo;
        depthStencil.DepthTestEnable = Vk.False;
        depthStencil.DepthWriteEnable = Vk.False;
        config.DepthStencilInfo = depthStencil;

        config.RenderPass = renderPass;
        config.PipelineLayout = pipelineLayout;

        pipeline = new StandardPipeline(vulkan.Vk, vulkan.Device, vertShaderPath, fragShaderPath, config, rendererName);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        pipeline?.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        pool.Dispose();
        setLayout.Dispose();
    }
}
