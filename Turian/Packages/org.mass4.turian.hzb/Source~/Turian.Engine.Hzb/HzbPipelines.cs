namespace Turian.Engine.Hzb;

static unsafe class HzbPipelines
{
    internal static void Check(Result result)
    {
        if (result != Result.Success) throw new VulkanException("HZB Vulkan operation failed: {0}", result);
    }

    internal static ShaderModule Shader(Vulkan vulkan, string name)
    {
        using var stream = typeof(HzbPipelines).Assembly.GetManifestResourceStream(
            "Turian.Engine.Hzb.Shaders." + name)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        fixed (byte* code = bytes)
        {
            ShaderModuleCreateInfo info = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)bytes.Length,
                PCode = (uint*)code
            };
            Check(vulkan.Vk.CreateShaderModule(vulkan.Device.VkDevice, in info, null, out var shader));
            return shader;
        }
    }

    internal static PipelineLayout Layout(Vulkan vulkan, Silk.NET.Vulkan.DescriptorSetLayout set,
        ShaderStageFlags stage, uint pushSize)
    {
        PushConstantRange push = new(stage, 0, pushSize);
        PipelineLayoutCreateInfo info = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &set,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push
        };
        Check(vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in info, null, out var layout));
        return layout;
    }

    internal static Pipeline Compute(Vulkan vulkan, string name, PipelineLayout layout)
    {
        var shader = Shader(vulkan, name);
        fixed (byte* entry = "main\0"u8)
        {
            ComputePipelineCreateInfo info = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = layout,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shader,
                    PName = entry
                }
            };
            var result = vulkan.Vk.CreateComputePipelines(vulkan.Device.VkDevice, default, 1, in info, null, out var pipeline);
            vulkan.Vk.DestroyShaderModule(vulkan.Device.VkDevice, shader, null);
            Check(result);
            return pipeline;
        }
    }

    internal static Pipeline Depth(Vulkan vulkan, RenderPass pass, PipelineLayout layout)
    {
        var shader = Shader(vulkan, "depth.vert.spv");
        var bindings = Vertex.GetBindingDescriptions();
        var attributes = Vertex.GetAttributeDescriptions();
        var config = new PipelineConfigInfo();
        StandardPipeline.DefaultPipelineConfigInfo(ref config);
        StandardPipeline.EnableMultiSampling(ref config, vulkan.Device.MsaaSamples);
        var blend = config.ColorBlendInfo;
        blend.AttachmentCount = 0;
        blend.PAttachments = null;
        var dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        PipelineDynamicStateCreateInfo dynamic = new()
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamics
        };
        fixed (byte* entry = "main\0"u8)
        fixed (VertexInputBindingDescription* binding = bindings)
        fixed (VertexInputAttributeDescription* attribute = attributes)
        {
            PipelineShaderStageCreateInfo stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = shader,
                PName = entry
            };
            PipelineVertexInputStateCreateInfo vertex = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindings.Length,
                PVertexBindingDescriptions = binding,
                VertexAttributeDescriptionCount = 1,
                PVertexAttributeDescriptions = attribute
            };
            GraphicsPipelineCreateInfo info = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 1,
                PStages = &stage,
                PVertexInputState = &vertex,
                PInputAssemblyState = &config.InputAssemblyInfo,
                PViewportState = &config.ViewportInfo,
                PRasterizationState = &config.RasterizationInfo,
                PMultisampleState = &config.MultisampleInfo,
                PDepthStencilState = &config.DepthStencilInfo,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
                RenderPass = pass,
                BasePipelineIndex = -1
            };
            var result = vulkan.Vk.CreateGraphicsPipelines(vulkan.Device.VkDevice, default, 1, in info, null, out var pipeline);
            vulkan.Vk.DestroyShaderModule(vulkan.Device.VkDevice, shader, null);
            Check(result);
            return pipeline;
        }
    }

    internal static void Barrier(Vulkan vulkan, CommandBuffer command, PipelineStageFlags source,
        PipelineStageFlags destination, AccessFlags read, AccessFlags write)
    {
        MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = read, DstAccessMask = write };
        vulkan.Vk.CmdPipelineBarrier(command, source, destination, 0, 1, in barrier, 0, null, 0, null);
    }
}
