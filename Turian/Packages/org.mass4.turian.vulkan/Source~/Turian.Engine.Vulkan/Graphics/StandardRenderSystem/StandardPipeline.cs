namespace Turian.Engine.Core;

/// <summary>
/// Represents a standard graphics pipeline for rendering purposes.
/// </summary>
public class StandardPipeline : IDisposable
{
    /// <summary>
    /// Gets the Vulkan graphics pipeline associated with this standard pipeline.
    /// </summary>
    public Pipeline VkPipeline => graphicsPipeline;

    /// <summary>
    /// Gets the type of the graphics pipeline (standard pipeline).
    /// </summary>
    public GraphicsPipelineTypes PipelineType => pipelineType;

    readonly Vk vk;
    readonly Device device;

    Pipeline graphicsPipeline;

    readonly GraphicsPipelineTypes pipelineType;

    ShaderModule vertShaderModule;
    ShaderModule fragShaderModule;

    /// <summary>
    /// Initializes a new instance of the <see cref="StandardPipeline"/> class for creating a standard
    /// vertex and fragment graphics pipeline.
    /// </summary>
    /// <param name="vk">The Vulkan context for graphics operations.</param>
    /// <param name="device">The Vulkan device used for rendering.</param>
    /// <param name="vertPath">The file path to the vertex shader.</param>
    /// <param name="fragPath">The file path to the fragment shader.</param>
    /// <param name="configInfo">The configuration information for the pipeline.</param>
    /// <param name="renderSystemName">The name of the rendering system (optional, default is "unknown").</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="vk"/> or <paramref name="device"/> is null.</exception>
    /// <remarks>
    /// This constructor initializes a standard vertex and fragment graphics pipeline. It compiles
    /// and links the provided vertex and fragment shader files and configures the pipeline based
    /// on the provided <paramref name="configInfo"/>.
    /// </remarks>
    public StandardPipeline(
        Vk vk,
        Device device,
        string vertPath,
        string fragPath,
        PipelineConfigInfo configInfo,
        string renderSystemName = "unknown"
    )
    {
        this.vk = vk;
        this.device = device;
        pipelineType = GraphicsPipelineTypes.Std;
        CreateGraphicsPipelineStd(vertPath, fragPath, configInfo, renderSystemName);
    }

    /// <summary>
    /// Binds the graphics pipeline to a Vulkan command buffer for rendering.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer.</param>
    public void Bind(CommandBuffer commandBuffer)
    {
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, graphicsPipeline);
    }

    /// <summary>
    /// Sets default pipeline configuration settings in the provided <paramref name="configInfo"/>.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration information to be modified.</param>
    public static unsafe void DefaultPipelineConfigInfo(ref PipelineConfigInfo configInfo)
    {
        var inputAssemblyInfo = configInfo.InputAssemblyInfo;
        inputAssemblyInfo.SType = StructureType.PipelineInputAssemblyStateCreateInfo;
        inputAssemblyInfo.Topology = PrimitiveTopology.TriangleList;
        inputAssemblyInfo.PrimitiveRestartEnable = Vk.False; //imgui
        configInfo.InputAssemblyInfo = inputAssemblyInfo;

        var viewportInfo = configInfo.ViewportInfo;
        viewportInfo.SType = StructureType.PipelineViewportStateCreateInfo;
        viewportInfo.ViewportCount = 1;
        viewportInfo.PViewports = default; // imgui
        viewportInfo.ScissorCount = 1;
        viewportInfo.PScissors = default; // imgui
        configInfo.ViewportInfo = viewportInfo;

        var rasterizationInfo = configInfo.RasterizationInfo;
        rasterizationInfo.SType = StructureType.PipelineRasterizationStateCreateInfo;
        rasterizationInfo.DepthClampEnable = Vk.False;
        rasterizationInfo.RasterizerDiscardEnable = Vk.False;
        rasterizationInfo.PolygonMode = PolygonMode.Fill;
        rasterizationInfo.LineWidth = 1f;
        rasterizationInfo.CullMode = CullModeFlags.None;
        rasterizationInfo.FrontFace = FrontFace.CounterClockwise;
        rasterizationInfo.DepthBiasEnable = Vk.False;
        rasterizationInfo.DepthBiasConstantFactor = 0f;
        rasterizationInfo.DepthBiasClamp = 0f;
        rasterizationInfo.DepthBiasSlopeFactor = 0f;
        configInfo.RasterizationInfo = rasterizationInfo;

        var multisampleInfo = configInfo.MultisampleInfo;
        multisampleInfo.SType = StructureType.PipelineMultisampleStateCreateInfo;
        multisampleInfo.SampleShadingEnable = Vk.False;
        multisampleInfo.RasterizationSamples = SampleCountFlags.Count1Bit;
        multisampleInfo.MinSampleShading = 1.0f;
        multisampleInfo.PSampleMask = default;
        multisampleInfo.AlphaToCoverageEnable = Vk.False;
        multisampleInfo.AlphaToOneEnable = Vk.False;
        configInfo.MultisampleInfo = multisampleInfo;

        var colorBlendAttachment = configInfo.ColorBlendAttachment;
        colorBlendAttachment.BlendEnable = Vk.False;
        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
        colorBlendAttachment.DstColorBlendFactor = BlendFactor.Zero;
        colorBlendAttachment.ColorBlendOp = BlendOp.Add;
        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.Zero;
        colorBlendAttachment.AlphaBlendOp = BlendOp.Add;
        colorBlendAttachment.ColorWriteMask =
            ColorComponentFlags.RBit
            | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit
            | ColorComponentFlags.ABit;
        configInfo.ColorBlendAttachment = colorBlendAttachment;

        var colorBlendInfo = configInfo.ColorBlendInfo;
        colorBlendInfo.SType = StructureType.PipelineColorBlendStateCreateInfo;
        colorBlendInfo.LogicOpEnable = Vk.False;
        colorBlendInfo.LogicOp = LogicOp.Copy;
        colorBlendInfo.AttachmentCount = 1;
        colorBlendInfo.PAttachments = (PipelineColorBlendAttachmentState*)
            Unsafe.AsPointer(ref configInfo.ColorBlendAttachment);
        colorBlendInfo.BlendConstants[0] = 0;
        colorBlendInfo.BlendConstants[1] = 0;
        colorBlendInfo.BlendConstants[2] = 0;
        colorBlendInfo.BlendConstants[3] = 0;
        configInfo.ColorBlendInfo = colorBlendInfo;

        var depthStencilInfo = configInfo.DepthStencilInfo;
        depthStencilInfo.SType = StructureType.PipelineDepthStencilStateCreateInfo;
        depthStencilInfo.DepthTestEnable = Vk.True;
        depthStencilInfo.DepthWriteEnable = Vk.True;
        depthStencilInfo.DepthCompareOp = CompareOp.Less;
        depthStencilInfo.DepthBoundsTestEnable = Vk.False;
        depthStencilInfo.MinDepthBounds = 0.0f;
        depthStencilInfo.MaxDepthBounds = 1.0f;
        depthStencilInfo.StencilTestEnable = Vk.False;
        depthStencilInfo.Front = default;
        depthStencilInfo.Back = default;
        configInfo.DepthStencilInfo = depthStencilInfo;

        configInfo.BindingDescriptions = Vertex.GetBindingDescriptions();
        configInfo.AttributeDescriptions = Vertex.GetAttributeDescriptions();
    }

    /// <summary>
    /// Enables alpha blending in the provided <paramref name="configInfo"/>.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration information to be modified.</param>
    public static void EnableAlphaBlending(ref PipelineConfigInfo configInfo)
    {
        var colorBlendAttachment = configInfo.ColorBlendAttachment;
        colorBlendAttachment.BlendEnable = Vk.True;
        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.SrcAlpha;
        colorBlendAttachment.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
        colorBlendAttachment.ColorBlendOp = BlendOp.Add;
        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
        colorBlendAttachment.AlphaBlendOp = BlendOp.Add;
        colorBlendAttachment.ColorWriteMask =
            ColorComponentFlags.RBit
            | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit
            | ColorComponentFlags.ABit;
        configInfo.ColorBlendAttachment = colorBlendAttachment;
    }

    /// <summary>
    /// Enables multisampling with the specified <paramref name="msaaSamples"/> in the provided <paramref name="configInfo"/>.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration information to be modified.</param>
    /// <param name="msaaSamples">The multisampling sample count flags.</param>
    public static void EnableMultiSampling(
        ref PipelineConfigInfo configInfo,
        SampleCountFlags msaaSamples
    )
    {
        var multisampleInfo = configInfo.MultisampleInfo;
        multisampleInfo.RasterizationSamples = msaaSamples;
        configInfo.MultisampleInfo = multisampleInfo;
    }

    /// <summary>
    /// Disposes of the standard graphics pipeline and associated resources.
    /// </summary>
    public unsafe void Dispose()
    {
        // Shader modules are already destroyed at the end of CreateGraphicsPipelineStd
        vk.DestroyPipeline(device.VkDevice, graphicsPipeline, null);

        GC.SuppressFinalize(this);
    }

    unsafe void CreateGraphicsPipelineStd(
        string vertPath,
        string fragPath,
        PipelineConfigInfo configInfo,
        string renderSystemName
    )
    {
        var vertBytes = FileUtil.GetShaderBytes(vertPath, renderSystemName);
        var fragBytes = FileUtil.GetShaderBytes(fragPath, renderSystemName);
        vertShaderModule = CreateShaderModule(vertBytes);
        fragShaderModule = CreateShaderModule(fragBytes);

        PipelineShaderStageCreateInfo vertShaderStageInfo =
            new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
                Flags = PipelineShaderStageCreateFlags.None,
                PNext = null,
                PSpecializationInfo = null,
            };

        PipelineShaderStageCreateInfo fragShaderStageInfo =
            new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
                Flags = PipelineShaderStageCreateFlags.None,
                PNext = null,
                PSpecializationInfo = null,
            };

        var shaderStages = stackalloc[] { vertShaderStageInfo, fragShaderStageInfo };

        var bindingDescriptions = configInfo.BindingDescriptions;
        var attributeDescriptions = configInfo.AttributeDescriptions;

        fixed (VertexInputBindingDescription* bindingDescriptionsPtr = bindingDescriptions)
        fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
        {
            var vertextInputInfo = new PipelineVertexInputStateCreateInfo()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                VertexBindingDescriptionCount = (uint)bindingDescriptions.Length,
                PVertexAttributeDescriptions = attributeDescriptionsPtr,
                PVertexBindingDescriptions = bindingDescriptionsPtr,
            };

            // stole this from ImGui controller, pulled this out of the default pipelineConfig and constructor
            Span<DynamicState> dynamicStates =
                [DynamicState.Viewport, DynamicState.Scissor];
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = (uint)dynamicStates.Length,
                PDynamicStates = (DynamicState*)Unsafe.AsPointer(ref dynamicStates[0])
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = shaderStages,
                PVertexInputState = &vertextInputInfo,
                PInputAssemblyState = &configInfo.InputAssemblyInfo,
                PViewportState = &configInfo.ViewportInfo,
                PRasterizationState = &configInfo.RasterizationInfo,
                PMultisampleState = &configInfo.MultisampleInfo,
                PColorBlendState = &configInfo.ColorBlendInfo,
                PDepthStencilState = &configInfo.DepthStencilInfo,
                PDynamicState = (PipelineDynamicStateCreateInfo*)
                    Unsafe.AsPointer(ref dynamicState),
                Layout = configInfo.PipelineLayout,
                RenderPass = configInfo.RenderPass,
                Subpass = configInfo.Subpass,
                BasePipelineIndex = -1,
                BasePipelineHandle = default
            };

            var resultVulkan = vk.CreateGraphicsPipelines(device.VkDevice, default, 1, in pipelineInfo, default, out graphicsPipeline);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create graphics pipeline: {0}", resultVulkan);
            }
        }

        vk.DestroyShaderModule(device.VkDevice, fragShaderModule, null);
        vk.DestroyShaderModule(device.VkDevice, vertShaderModule, null);

        _ = SilkMarshal.Free((nint)shaderStages[0].PName);
        _ = SilkMarshal.Free((nint)shaderStages[1].PName);
    }

    unsafe ShaderModule CreateShaderModule(byte[] code)
    {
        ShaderModuleCreateInfo createInfo =
            new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, };

        ShaderModule shaderModule;

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;

            if (
                vk.CreateShaderModule(device.VkDevice, in createInfo, null, out shaderModule)
                != Result.Success
            )
            {
                throw new VulkanException("failed to create shader module!");
            }
        }

        return shaderModule;
    }
}
