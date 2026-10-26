namespace Turian.Engine.Core;

public sealed partial class WorldGraphicsRenderSystem
{
    unsafe void CreatePipelineLayout(DescriptorSetLayout globalLayout)
    {
        var layouts = stackalloc Silk.NET.Vulkan.DescriptorSetLayout[2];
        layouts[0] = globalLayout.GetDescriptorSetLayout();
        layouts[1] = textureLayout.GetDescriptorSetLayout();
        var push = new PushConstantRange(ShaderStageFlags.FragmentBit, 0, sizeof(int));
        PipelineLayoutCreateInfo info = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push,
        };
        if (vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in info, null, out pipelineLayout) != Result.Success)
        {
            throw new VulkanException("Vulkan: failed to create world-graphic pipeline layout");
        }
    }

    void CreatePipeline(RenderPass renderPass)
    {
        var config = new PipelineConfigInfo();
        StandardPipeline.DefaultPipelineConfigInfo(ref config);
        StandardPipeline.EnableAlphaBlending(ref config);
        StandardPipeline.EnableMultiSampling(ref config, vulkan.Device.MsaaSamples);
        config.BindingDescriptions = [new(0, (uint)Unsafe.SizeOf<WorldGraphicVertex>(), VertexInputRate.Vertex)];
        config.AttributeDescriptions =
        [
            Attribute(0, Format.R32G32B32Sfloat, nameof(WorldGraphicVertex.Position)),
            Attribute(1, Format.R32G32B32A32Sfloat, nameof(WorldGraphicVertex.Color)),
            Attribute(2, Format.R32G32Sfloat, nameof(WorldGraphicVertex.Uv)),
        ];
        var depth = config.DepthStencilInfo;
        depth.DepthWriteEnable = Vk.False;
        config.DepthStencilInfo = depth;
        config.RenderPass = renderPass;
        config.PipelineLayout = pipelineLayout;
        pipeline = new StandardPipeline(vulkan.Vk, vulkan.Device,
            "worldgraphic.vert.spv", "worldgraphic.frag.spv", config, "WorldGraphicsRenderer");
    }

    static VertexInputAttributeDescription Attribute(uint location, Format format, string property) =>
        new(location, 0, format, property switch
        {
            nameof(WorldGraphicVertex.Position) => 0,
            nameof(WorldGraphicVertex.Color) => 12,
            _ => 28,
        });
}
