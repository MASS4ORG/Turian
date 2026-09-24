namespace Turian.Engine.Core;

/// <summary>
/// Mesh Renderer can draw custom created solids
/// </summary>
public class MeshRenderSystem : IRenderSystem
{
    readonly Vulkan vulkan;

    const string taskShaderPath = "testMesh.task.spv";
    const string meshShaderPath = "testMesh.mesh.spv";
    const string fragShaderPath = "testMesh.frag.spv";
    const string rendererName = "Mesh2Renderer";

    MeshPipeline pipeline = null!;
    PipelineLayout pipelineLayout;

    /// <summary>
    /// .ctor
    /// </summary>
    /// <param name="vulkan"></param>
    /// <param name="renderPass"></param>
    /// <param name="globalSetLayout"></param>
    public MeshRenderSystem(
        Vulkan vulkan,
        RenderPass renderPass,
        Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout
    )
    {
        this.vulkan = vulkan;
        CreatePipelineLayout(globalSetLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc />
    public unsafe void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        pipeline.Bind(frameInfo.CommandBuffer);

        var descriptorSet = frameInfo.GlobalDescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(
            frameInfo.CommandBuffer,
            PipelineBindPoint.Graphics,
            pipelineLayout,
            0,
            1,
            in descriptorSet,
            0,
            null
        );

        foreach (var rootNode in frameInfo.Nodes.Where(static node => node.IsActive))
        {
            foreach (var component in rootNode.GetComponentsInChildren<MeshComponent>())
            {
                StandardPushConstantData push = new()
                {
                    ModelMatrix = component.TransformationMatrix(),
                };
                vulkan.Vk.CmdPushConstants(
                    frameInfo.CommandBuffer,
                    pipelineLayout,
                    ShaderStageFlags.FragmentBit
                    | ShaderStageFlags.MeshBitNV
                    | ShaderStageFlags.TaskBitNV,
                    0,
                    StandardPushConstantData.SizeOf(),
                    ref push
                );
                component.Draw(frameInfo.CommandBuffer);
            }
        }
    }

    /// <inheritdoc />
    public unsafe void Dispose()
    {
        pipeline.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        GC.SuppressFinalize(this);
    }

    unsafe void CreatePipelineLayout(Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout)
    {
        Silk.NET.Vulkan.DescriptorSetLayout[] descriptorSetLayouts = [globalSetLayout];
        PushConstantRange pushConstantRange = new()
        {
            StageFlags =
                ShaderStageFlags.FragmentBit
                | ShaderStageFlags.MeshBitNV
                | ShaderStageFlags.TaskBitNV,
            Offset = 0,
            Size = StandardPushConstantData.SizeOf(),
        };

        fixed (Silk.NET.Vulkan.DescriptorSetLayout* descriptorSetLayoutPtr = descriptorSetLayouts)
        {
            PipelineLayoutCreateInfo pipelineLayoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = descriptorSetLayoutPtr,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange,
            };

            var resultVulkan = vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in pipelineLayoutInfo, null,
                out pipelineLayout);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create pipeline layout: {0}", resultVulkan);
            }
        }
    }

    void CreatePipeline(RenderPass renderPass)
    {
        Debug.Assert(pipelineLayout.Handle != 0, "Cannot create pipeline before pipeline layout");

        var pipelineConfig = new PipelineConfigInfo();
        MeshPipeline.DefaultPipelineConfigInfo(ref pipelineConfig);
        MeshPipeline.EnableMultiSampling(ref pipelineConfig, vulkan.Device.MsaaSamples);

        var inputAssemblyInfo = pipelineConfig.InputAssemblyInfo;
        inputAssemblyInfo.Topology = PrimitiveTopology.TriangleStrip;
        pipelineConfig.InputAssemblyInfo = inputAssemblyInfo;

        pipelineConfig.RenderPass = renderPass;
        pipelineConfig.PipelineLayout = pipelineLayout;
        pipeline = new MeshPipeline(
            vulkan.Vk,
            vulkan.Device,
            taskShaderPath,
            meshShaderPath,
            fragShaderPath,
            pipelineConfig,
            rendererName
        );
    }
}
