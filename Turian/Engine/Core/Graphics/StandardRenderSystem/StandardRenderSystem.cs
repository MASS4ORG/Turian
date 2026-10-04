namespace Turian.Engine.Core;

/// <summary>
/// PBR forward render system. Binds the global UBO at set 0 and, for every submesh drawn,
/// the material descriptor set at set 1. Materials come from the model's material slots,
/// falling back to a default material for submeshes that declare none. Draws are sorted by material, then model,
/// so each is bound once per run of draws that share it.
/// </summary>
public class StandardRenderSystem : IRenderSystem
{
    readonly Vulkan vulkan;
    const string vertShaderPath = "standardShader.vert.spv";
    const string fragShaderPath = "standardShader.frag.spv";

    StandardPipeline pipeline = null!;
    PipelineLayout pipelineLayout;
    readonly MaterialDescriptorContext materials;
    readonly RenderList renderList = new();
    readonly Dictionary<(Guid Model, int Index), MaterialAsset> derivedMaterials = [];
    readonly Dictionary<Guid, MaterialAsset> overrideMaterials = [];
    readonly ConditionalWeakTable<ModelComponent, WorldBoundsCache> worldBounds = [];
    readonly Plane[] frustumPlanes = new Plane[6];
    bool prepared;
    readonly Silk.NET.Vulkan.DescriptorSetLayout globalLayout;
    IOcclusionCuller? occlusion;
    OcclusionDraw[] candidates = [];
    bool indirect;

    /// <summary>Optional visibility factory; the installed Brick is discovered when no factory is supplied.</summary>
    public IOcclusionCullingFactory? OcclusionCullingFactory { get; set; }

    /// <summary>Enables the optional GPU visibility pass for this view; disabled by default.</summary>
    public bool UseOcclusionCulling { get; set; }

    /// <summary>Counts from the latest completed visibility pass, which may lag by the frame slot count.</summary>
    public RenderCullingStats OcclusionStats => occlusion?.CompletedStats ?? default;

    /// <summary>Bytes allocated for optional visibility resources.</summary>
    public ulong OcclusionAllocatedBytes => occlusion?.AllocatedBytes ?? 0;

    /// <summary>Gets or sets whether submeshes outside the camera frustum are rejected before material resolution.</summary>
    public bool UseFrustumCulling { get; set; } = true;

    /// <summary>Gets the submitted and culled submesh counts from the latest prepared frame.</summary>
    public RenderCullingStats CullingStats { get; private set; }

    /// <summary>
    /// .ctor
    /// </summary>
    /// <param name="vulkan"></param>
    /// <param name="assets">The asset database materials and textures are read from.</param>
    /// <param name="renderPass"></param>
    /// <param name="globalSetLayout"></param>
    public StandardRenderSystem(
        Vulkan vulkan,
        AssetDatabase assets,
        RenderPass renderPass,
        Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout
    )
    {
        this.vulkan = vulkan;
        globalLayout = globalSetLayout;
        materials = new MaterialDescriptorContext(vulkan, assets);
        OcclusionCullingFactory = OcclusionCullers.Find();
        UseOcclusionCulling = OcclusionCullingFactory?.IsEnabled(assets) ?? false;
        CreatePipelineLayout(globalSetLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc />
    public void Prepare(FrameInfo frameInfo, GlobalUbo ubo)
    {
        ArgumentNullException.ThrowIfNull(ubo);

        renderList.Gather(frameInfo.Nodes, frameInfo.Camera.CullingMask);
        UpdateLights(renderList.Lights, ubo);
        if (UseFrustumCulling && renderList.Models.Count != 0)
            GeometryUtility.CalculateFrustumPlanes(
                frameInfo.Camera.GetViewMatrix() * frameInfo.Camera.GetProjectionMatrix(), frustumPlanes);
        CullingStats = default;
        BuildDraws();
        prepared = true;
        indirect = false;
    }

    /// <inheritdoc />
    public void RecordBeforeRenderPass(FrameInfo frameInfo)
    {
        if (!UseOcclusionCulling || renderList.Draws.Count == 0) return;
        occlusion ??= CreateOcclusionCuller();
        if (occlusion is null) return;
        FillOcclusionCandidates();
        indirect = occlusion.Record(frameInfo, candidates.AsSpan(0, renderList.Draws.Count));
    }

    IOcclusionCuller? CreateOcclusionCuller() =>
        (OcclusionCullingFactory ?? OcclusionCullers.Find())?.Create(vulkan, globalLayout);

    void FillOcclusionCandidates()
    {
        var count = renderList.Draws.Count;
        if (candidates.Length < count) candidates = new OcclusionDraw[count];
        for (var i = 0; i < count; i++)
        {
            var draw = renderList.Draws[i];
            candidates[i] = new OcclusionDraw(draw.Model, draw.SubMesh, draw.ModelMatrix, draw.WorldBounds);
        }
    }

    /// <inheritdoc />
    public unsafe void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        // A host that skips Prepare still draws; its lights then reach the GPU a frame late.
        if (!prepared) Prepare(frameInfo, ubo);
        prepared = false;

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

        RecordDraws(frameInfo.CommandBuffer);

        // Every material a visible scene needs has resolved by the end of the first frame, so this
        // reports the settled total once rather than counting textures as they stream in.
        TextureMemoryReport.ReportIfChanged(vulkan.Device);
    }

    void BuildDraws()
    {
        foreach (var component in renderList.Models)
        {
            if (component.ModelInstance is { } model && component.Node is { } node)
                AddDraws(component, model, node.GlobalTransform);
        }

        renderList.Sort();
        CullingStats = CullingStats with { Submitted = renderList.Draws.Count };
    }

    void AddDraws(ModelComponent component, Model model, Transform global)
    {
        var start = 0;
        var last = model.SubMeshes.Count;
        if (component.TryGetSubMeshRange(out var rangeStart, out var rangeCount))
        {
            start = rangeStart;
            last = Math.Min(rangeStart + rangeCount, model.SubMeshes.Count);
        }

        var cached = worldBounds.GetValue(component, static _ => new WorldBoundsCache());
        cached.Update(model, global, start, Math.Max(0, last - start));
        var modelAssetId = component.ModelAssetId;
        var modelOrder = renderList.OrderOf(model);
        for (var i = start; i < last; i++)
        {
            if (UseFrustumCulling && !GeometryUtility.TestPlanesAABB(frustumPlanes, cached.Bounds[i - start]))
            {
                CullingStats = CullingStats with { Culled = CullingStats.Culled + 1 };
                continue;
            }

            var slot = i - start;
            var overrideReference = slot < component.Materials.Count ? component.Materials[slot] : null;
            var material = ResolveMaterial(modelAssetId, model.SubMeshes[i].MaterialIndex, overrideReference);
            var key = RenderList.SortKey(renderList.OrderOf(material), modelOrder);
            renderList.Draws.Add(new DrawItem(model, i, material, cached.ModelMatrix, cached.NormalMatrix, key,
                renderList.Draws.Count, cached.Bounds[i - start], component.Node!.RenderLayer));
        }
    }

    void RecordDraws(CommandBuffer commandBuffer)
    {
        Model? boundModel = null;
        MaterialResource? boundMaterial = null;
        var drawIndex = 0;
        foreach (ref readonly var draw in CollectionsMarshal.AsSpan(renderList.Draws))
        {
            if (!ReferenceEquals(draw.Material, boundMaterial))
            {
                BindMaterial(commandBuffer, draw.Material);
                boundMaterial = draw.Material;
            }

            if (!ReferenceEquals(draw.Model, boundModel))
            {
                draw.Model.Bind(commandBuffer);
                boundModel = draw.Model;
            }

            StandardPushConstantData push = new() { ModelMatrix = draw.ModelMatrix, NormalMatrix = draw.NormalMatrix };
            var normal = push.NormalMatrix;
            // The normal matrix's unused final element carries layer bits within the 128-byte push constant limit.
            normal.M44 = BitConverter.UInt32BitsToSingle(LayerMask.FromLayer(draw.RenderLayer).Value);
            push.NormalMatrix = normal;
            vulkan.Vk.CmdPushConstants(
                commandBuffer,
                pipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                StandardPushConstantData.SizeOf(),
                ref push
            );
            if (indirect)
                draw.Model.DrawSubMeshIndirect(commandBuffer, occlusion!.IndirectBuffer, (ulong)drawIndex * 20);
            else
                draw.Model.DrawSubMesh(commandBuffer, draw.SubMesh);
            drawIndex++;
        }
    }

    unsafe void BindMaterial(CommandBuffer commandBuffer, MaterialResource material)
    {
        var materialSet = material.DescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Graphics,
            pipelineLayout,
            firstSet: 1,
            descriptorSetCount: 1,
            in materialSet,
            0,
            null
        );
    }

    /// <summary>
    /// Resolves the material for one submesh: an override slot first, then the child material
    /// the importer derived from the source file, then the default material. Material handles are kept per id, so
    /// steady frames neither allocate nor re-derive ids.
    /// </summary>
    MaterialResource ResolveMaterial(
        Guid modelAssetId,
        int? materialIndex,
        AssetReference<MaterialAsset>? overrideReference) =>
        OverrideMaterial(overrideReference) ?? DerivedMaterial(modelAssetId, materialIndex) ?? materials.DefaultMaterial;

    MaterialResource? OverrideMaterial(AssetReference<MaterialAsset>? reference)
    {
        if (reference is not { IsEmpty: false }) return null;
        if (!overrideMaterials.TryGetValue(reference.AssetId, out var handle))
            overrideMaterials[reference.AssetId] = handle = new MaterialAsset { Id = reference.AssetId };
        return handle.GetContent(materials);
    }

    MaterialResource? DerivedMaterial(Guid modelAssetId, int? materialIndex)
    {
        if (modelAssetId == Guid.Empty || materialIndex is not { } index) return null;
        if (!derivedMaterials.TryGetValue((modelAssetId, index), out var handle))
        {
            var id = AssetIdFactory.Derive(modelAssetId, $"material:{index}");
            derivedMaterials[(modelAssetId, index)] = handle = new MaterialAsset { Id = id };
        }

        return handle.GetContent(materials);
    }

    /// <summary>Fills the UBO's light slots from <paramref name="lights"/>, clearing slots no light uses.</summary>
    /// <param name="lights">The active lights, in scene order; lights beyond the slot count are ignored.</param>
    /// <param name="ubo">The global UBO to fill.</param>
    internal static void UpdateLights(List<LightComponent> lights, GlobalUbo ubo)
    {
        // Slots are filled from scratch every frame: a slot left over from a scene with more lights
        // would keep shining, and a default point light sits on the world origin at full intensity.
        ubo.ClearLights();

        var pointIndex = 0;
        var directionalIndex = 0;
        foreach (var component in lights)
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
                ubo.SetDirectionalLightMask(directionalIndex++, component.CullingMask);
                continue;
            }

            if (pointIndex == ubo.Count) continue;

            ubo.SetPointLightPosition(pointIndex, lightNode.GlobalTransform.Position);
            ubo.SetPointLightColor(pointIndex, component.Color, component.Intensity);
            ubo.SetPointLightMask(pointIndex++, component.CullingMask);
        }
    }

    /// <inheritdoc />
    public unsafe void Dispose()
    {
        renderList.Reset();
        occlusion?.Dispose();
        worldBounds.Clear();
        derivedMaterials.Clear();
        overrideMaterials.Clear();
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
