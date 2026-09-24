namespace Turian.Engine.Core;

/// <summary>
/// PBR forward render system. Binds the global UBO at set 0 and, for every submesh drawn,
/// the material descriptor set at set 1. Materials come from the model's material slots,
/// falling back to a default material for submeshes that declare none.
/// </summary>
public class StandardRenderSystem : IRenderSystem
{
    readonly Vulkan vulkan;
    const string vertShaderPath = "standardShader.vert.spv";
    const string fragShaderPath = "standardShader.frag.spv";

    StandardPipeline pipeline = null!;
    PipelineLayout pipelineLayout;
    readonly MaterialDescriptorContext materials;

    /// <summary>
    /// .ctor
    /// </summary>
    /// <param name="vulkan"></param>
    /// <param name="renderPass"></param>
    /// <param name="globalSetLayout"></param>
    public StandardRenderSystem(
        Vulkan vulkan,
        RenderPass renderPass,
        Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout
    )
    {
        this.vulkan = vulkan;
        materials = new MaterialDescriptorContext(vulkan);
        CreatePipelineLayout(globalSetLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc />
    public unsafe void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        ArgumentNullException.ThrowIfNull(ubo);

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

        UpdateLights(frameInfo, ubo);
        DrawSolids(frameInfo);

        // Every material a visible scene needs has resolved by the end of the first frame, so this
        // reports the settled total once rather than counting textures as they stream in.
        TextureMemoryReport.ReportIfChanged(vulkan.Device);
    }

    unsafe void DrawSolids(FrameInfo frameInfo)
    {
        foreach (var node in frameInfo.Nodes.Where(static node => node.IsActive))
        {
            foreach (var component in node.GetComponentsInChildren<ModelComponent>())
            {
                var model = component.ModelInstance;
                if (model is null)
                {
                    continue;
                }

                if (component.Node is not { } modelNode)
                {
                    continue;
                }

                StandardPushConstantData push = new()
                {
                    ModelMatrix = modelNode.GlobalTransform.Matrix4X4(),
                    NormalMatrix = modelNode.GlobalTransform.NormalMatrix()
                };
                vulkan.Vk.CmdPushConstants(
                    frameInfo.CommandBuffer,
                    pipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0,
                    StandardPushConstantData.SizeOf(),
                    ref push
                );
                model.Bind(frameInfo.CommandBuffer);

                var start = 0;
                var last = model.SubMeshes.Count;
                if (component.TryGetSubMeshRange(out var rangeStart, out var rangeCount))
                {
                    start = rangeStart;
                    last = Math.Min(rangeStart + rangeCount, model.SubMeshes.Count);
                }

                var modelAssetId = component.ModelAssetId;
                for (var i = start; i < last; i++)
                {
                    var slot = i - start;
                    var overrideReference = slot < component.Materials.Count ? component.Materials[slot] : null;
                    var material = ResolveMaterial(modelAssetId, model.SubMeshes[i].MaterialIndex, overrideReference);

                    var materialSet = material.DescriptorSet;
                    vulkan.Vk.CmdBindDescriptorSets(
                        frameInfo.CommandBuffer,
                        PipelineBindPoint.Graphics,
                        pipelineLayout,
                        firstSet: 1,
                        descriptorSetCount: 1,
                        in materialSet,
                        0,
                        null
                    );

                    model.DrawSubMesh(frameInfo.CommandBuffer, i);
                }
            }
        }
    }

    /// <summary>
    /// Resolves the material for one submesh: an override slot first, then the child material
    /// the importer derived from the source file, then the default material.
    /// </summary>
    MaterialResource ResolveMaterial(
        Guid modelAssetId,
        int? materialIndex,
        AssetReference<MaterialAsset>? overrideReference)
    {
        if (overrideReference is { IsEmpty: false })
        {
            var overridden = new MaterialAsset { Id = overrideReference.AssetId }.GetContent(materials);
            if (overridden is not null)
            {
                return overridden;
            }
        }

        if (modelAssetId == Guid.Empty || materialIndex is null)
        {
            return materials.DefaultMaterial;
        }

        var childId = AssetIdFactory.Derive(modelAssetId, $"material:{materialIndex.Value}");
        return new MaterialAsset { Id = childId }.GetContent(materials) ?? materials.DefaultMaterial;
    }

    static void UpdateLights(FrameInfo frameInfo, GlobalUbo ubo)
    {
        // Slots are filled from scratch every frame: a slot left over from a scene with more lights
        // would keep shining, and a default point light sits on the world origin at full intensity.
        ubo.ClearLights();

        var pointIndex = 0;
        var directionalIndex = 0;

        foreach (var rootNode in frameInfo.Nodes.Where(static node => node.IsActive))
        {
            foreach (var component in rootNode.GetComponentsInChildren<LightComponent>())
            {
                if (component.Node is not { } lightNode) continue;

                if (component.Type == LightType.Directional)
                {
                    if (directionalIndex == ubo.DirectionalCount) continue;

                    ubo.SetDirectionalLight(
                        directionalIndex,
                        lightNode.GlobalTransform.Forward,
                        component.Color,
                        component.Intensity);
                    directionalIndex++;
                    continue;
                }

                if (pointIndex == ubo.Count) continue;

                ubo.SetPointLightPosition(pointIndex, lightNode.GlobalTransform.Position);
                ubo.SetPointLightColor(pointIndex, component.Color, component.Intensity);
                pointIndex++;
            }
        }
    }

    /// <inheritdoc />
    public unsafe void Dispose()
    {
        pipeline.Dispose();
        materials.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        GC.SuppressFinalize(this);
    }

    unsafe void CreatePipelineLayout(Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout)
    {
        Silk.NET.Vulkan.DescriptorSetLayout[] descriptorSetLayouts =
        [
            globalSetLayout,
            materials.SetLayout.GetDescriptorSetLayout(),
        ];

        PushConstantRange pushConstantRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
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

            var resultVulkan = vulkan.Vk.CreatePipelineLayout(vulkan.Device.VkDevice, in pipelineLayoutInfo, null, out pipelineLayout);
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
        StandardPipeline.DefaultPipelineConfigInfo(ref pipelineConfig);

        StandardPipeline.EnableMultiSampling(ref pipelineConfig, vulkan.Device.MsaaSamples);

        pipelineConfig.RenderPass = renderPass;
        pipelineConfig.PipelineLayout = pipelineLayout;
        pipeline = new StandardPipeline(
            vulkan.Vk,
            vulkan.Device,
            vertShaderPath,
            fragShaderPath,
            pipelineConfig
        );
    }
}
