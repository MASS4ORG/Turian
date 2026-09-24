namespace Turian.Engine.Core;

/// <summary>
/// Represents a Vulkan graphics pipeline for mesh rendering.
/// </summary>
public class MeshPipeline : IDisposable
{
    /// <summary>
    /// Graphics pipeline
    /// </summary>
    public Pipeline VkPipeline => graphicsPipeline;

    readonly Vk vk;
    readonly Device device;
    Pipeline graphicsPipeline;
    ShaderModule taskShaderModule;
    ShaderModule meshShaderModule;
    ShaderModule fragShaderModule;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeshPipeline"/> class for creating a graphics pipeline
    /// tailored for mesh rendering tasks.
    /// </summary>
    /// <param name="vk">The Vulkan context for graphics operations.</param>
    /// <param name="device">The Vulkan device used for rendering.</param>
    /// <param name="taskPath">The file path to the mesh rendering task shader.</param>
    /// <param name="meshPath">The file path to the mesh shader.</param>
    /// <param name="fragPath">The file path to the fragment shader.</param>
    /// <param name="configInfo">The configuration information for the pipeline.</param>
    /// <param name="renderSystemName">The name of the rendering system (optional, default is "unknown").</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="vk"/> or <paramref name="device"/> is null.</exception>
    /// <remarks>
    /// This constructor initializes a graphics pipeline specifically designed for mesh rendering tasks.
    /// It compiles and links the provided shader files and configures the pipeline based on the provided
    /// <paramref name="configInfo"/>.
    /// </remarks>
    public MeshPipeline(
        Vk vk,
        Device device,
        string taskPath,
        string meshPath,
        string fragPath,
        PipelineConfigInfo configInfo,
        string renderSystemName = "unknown"
    )
    {
        this.vk = vk;
        this.device = device;
        CreateGraphicsPipelineMesh(taskPath, meshPath, fragPath, configInfo, renderSystemName);
    }

    /// <summary>
    /// Binds this graphics pipeline to a command buffer.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to bind the pipeline to.</param>
    public void Bind(CommandBuffer commandBuffer)
    {
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, graphicsPipeline);
    }

    /// <summary>
    /// Sets default values for the given pipeline configuration.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration to set default values for.</param>
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
    }

    /// <summary>
    /// Enables alpha blending in the given pipeline configuration.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration to enable alpha blending for.</param>
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
    /// Enables multi-sampling with the specified sample count in the given pipeline configuration.
    /// </summary>
    /// <param name="configInfo">The pipeline configuration to enable multi-sampling for.</param>
    /// <param name="msaaSamples">The sample count for multi-sampling.</param>
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
    /// Releases the resources used by this graphics pipeline.
    /// </summary>
    public unsafe void Dispose()
    {
        // Shader modules are already destroyed at the end of CreateGraphicsPipelineMesh
        vk.DestroyPipeline(device.VkDevice, graphicsPipeline, null);
        GC.SuppressFinalize(this);
    }

    unsafe void CreateGraphicsPipelineMesh(
        string taskPath,
        string meshPath,
        string fragPath,
        PipelineConfigInfo configInfo,
        string renderSystemName
    )
    {
        // Load shader bytes
        var taskBytes = FileUtil.GetShaderBytes(taskPath, renderSystemName);
        var meshBytes = FileUtil.GetShaderBytes(meshPath, renderSystemName);
        var fragBytes = FileUtil.GetShaderBytes(fragPath, renderSystemName);

        // Create shader modules
        taskShaderModule = CreateShaderModule(taskBytes);
        meshShaderModule = CreateShaderModule(meshBytes);
        fragShaderModule = CreateShaderModule(fragBytes);

        // Create shader stage info
        var shaderStages =
            stackalloc PipelineShaderStageCreateInfo[] {
            CreateShaderStageInfo(ShaderStageFlags.TaskBitNV, taskShaderModule),
            CreateShaderStageInfo(ShaderStageFlags.MeshBitNV, meshShaderModule),
            CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragShaderModule)
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
            StageCount = 3,
            PStages = shaderStages,
            PVertexInputState = null,
            PInputAssemblyState = null,
            PViewportState = &configInfo.ViewportInfo,
            PRasterizationState = &configInfo.RasterizationInfo,
            PMultisampleState = &configInfo.MultisampleInfo,
            PColorBlendState = &configInfo.ColorBlendInfo,
            PDepthStencilState = &configInfo.DepthStencilInfo,
            PDynamicState = (PipelineDynamicStateCreateInfo*)Unsafe.AsPointer(ref dynamicState),
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

        // Cleanup
        CleanupShaderModules();

        _ = SilkMarshal.Free((nint)shaderStages[0].PName);
        _ = SilkMarshal.Free((nint)shaderStages[1].PName);
        _ = SilkMarshal.Free((nint)shaderStages[2].PName);
    }

    unsafe ShaderModule CreateShaderModule(byte[] code)
    {
        ShaderModuleCreateInfo createInfo = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, };

        ShaderModule shaderModule;

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;

            if (vk.CreateShaderModule(device.VkDevice, in createInfo, null, out shaderModule) != Result.Success
            )
            {
                throw new VulkanException("");
            }
        }

        return shaderModule;
    }

    static unsafe PipelineShaderStageCreateInfo CreateShaderStageInfo(
        ShaderStageFlags stage,
        ShaderModule module
    )
    {
        return new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = stage,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main"),
            Flags = PipelineShaderStageCreateFlags.None,
            PNext = null,
            PSpecializationInfo = null,
        };
    }

    unsafe void CleanupShaderModules()
    {
        vk.DestroyShaderModule(device.VkDevice, taskShaderModule, null);
        vk.DestroyShaderModule(device.VkDevice, meshShaderModule, null);
        vk.DestroyShaderModule(device.VkDevice, fragShaderModule, null);
    }
}
