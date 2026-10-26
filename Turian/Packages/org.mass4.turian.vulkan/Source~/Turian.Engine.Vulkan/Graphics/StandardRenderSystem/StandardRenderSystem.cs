namespace Turian.Engine.Core;

/// <summary>
/// PBR forward render system. Binds the global UBO at set 0, the material descriptor set at set 1 and the
/// per-instance data at set 2. Materials come from the model's material slots, falling back to a default material
/// for submeshes that declare none. Draws are sorted by material, model, then submesh, so each is bound once per run
/// and every run of the same submesh becomes one instanced draw; <see cref="InstancedModelComponent"/> batches draw
/// from their own instance buffers.
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
    readonly List<(ModelComponent? Component, WorldBoundsCache? Bounds)> preparedBounds = [];
    readonly Plane[] frustumPlanes = new Plane[6];
    bool prepared;
    readonly Silk.NET.Vulkan.DescriptorSetLayout globalLayout;
    IOcclusionCuller? occlusion;
    OcclusionDraw[] candidates = [];
    bool indirect;
    readonly DescriptorSetLayout instanceLayout;
    readonly RetiredResources retired = new();
    readonly InstanceBatches batches;
    readonly InstanceBuffer?[] frameInstances = new InstanceBuffer?[SwapChain.MaxFramesInFlight];

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

    /// <summary>Enables optional draw and material accounting for this view.</summary>
    public bool CollectStatistics { get; set; }

    /// <summary>Submitted standard geometry counts from the latest instrumented frame.</summary>
    public RenderFrameStats FrameStats { get; private set; }

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
        instanceLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.StorageBuffer, ShaderStageFlags.VertexBit)
            .Build();
        batches = new InstanceBatches(vulkan, instanceLayout, retired);
        CreatePipelineLayout(globalSetLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc />
    public void Prepare(FrameInfo frameInfo, GlobalUbo ubo)
    {
        ArgumentNullException.ThrowIfNull(ubo);

        renderList.Gather(frameInfo.Nodes, frameInfo.Camera.CullingMask);
        UpdateLights(renderList.Lights, ubo);
        if (UseFrustumCulling && (renderList.Models.Count != 0 || renderList.InstancedModels.Count != 0))
        {
            GeometryUtility.CalculateFrustumPlanes(
                frameInfo.Camera.GetViewMatrix() * frameInfo.Camera.GetProjectionMatrix(), frustumPlanes);
        }

        CullingStats = default;
        retired.NextFrame();
        BuildDraws();
        var stats = CullingStats;
        batches.Prepare(renderList.InstancedModels, UseFrustumCulling ? frustumPlanes : [], ref stats);
        CullingStats = stats;
        prepared = true;
        indirect = false;
    }

    /// <inheritdoc />
    public void RecordBeforeRenderPass(FrameInfo frameInfo)
    {
        batches.Upload(frameInfo.CommandBuffer, frameInfo.FrameIndex);

        // Indirect commands start at their own instance; without that feature the CPU-culled draws stay direct.
        if (!UseOcclusionCulling || !vulkan.Device.SupportsDrawIndirectFirstInstance
                                 || renderList.Draws.Count == 0)
        {
            return;
        }

        occlusion ??= CreateOcclusionCuller();
        if (occlusion is null)
        {
            return;
        }

        FillOcclusionCandidates();
        indirect = occlusion.Record(frameInfo, candidates.AsSpan(0, renderList.Draws.Count));
    }

    IOcclusionCuller? CreateOcclusionCuller() =>
        (OcclusionCullingFactory ?? OcclusionCullers.Find())?.Create(vulkan, globalLayout);

    void FillOcclusionCandidates()
    {
        var count = renderList.Draws.Count;
        if (candidates.Length < count)
        {
            candidates = new OcclusionDraw[count];
        }

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
        if (!prepared)
        {
            Prepare(frameInfo, ubo);
        }

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

        RecordDraws(frameInfo.CommandBuffer, frameInfo.FrameIndex);

        // Every material a visible scene needs has resolved by the end of the first frame, so this
        // reports the settled total once rather than counting textures as they stream in.
        TextureMemoryReport.ReportIfChanged(vulkan.Device);
    }

    void BuildDraws()
    {
        while (preparedBounds.Count < renderList.Models.Count)
        {
            preparedBounds.Add(default);
        }

        if (preparedBounds.Count > renderList.Models.Count)
        {
            preparedBounds.RemoveRange(renderList.Models.Count, preparedBounds.Count - renderList.Models.Count);
        }

        var order = new DrawOrder();
        var bounds = CollectionsMarshal.AsSpan(preparedBounds);
        for (var i = 0; i < renderList.Models.Count; i++)
        {
            var component = renderList.Models[i];
            if (component.ModelInstance is VulkanModel model && component.Node is { } node)
            {
                ref var entry = ref bounds[i];
                if (!ReferenceEquals(entry.Component, component))
                {
                    entry = (component, worldBounds.GetValue(component, static _ => new WorldBoundsCache()));
                }

                AddDraws(component, model, node.GlobalTransform, entry.Bounds!, ref order);
            }
            else
            {
                bounds[i] = default;
            }
        }

        renderList.Sort();
        CullingStats = CullingStats with { Submitted = renderList.Draws.Count };
    }

    void AddDraws(ModelComponent component, VulkanModel model, Transform global, WorldBoundsCache cached,
        ref DrawOrder order)
    {
        var start = 0;
        var last = model.SubMeshes.Count;
        if (component.TryGetSubMeshRange(out var rangeStart, out var rangeCount))
        {
            start = rangeStart;
            last = Math.Min(rangeStart + rangeCount, model.SubMeshes.Count);
        }

        cached.Update(model, global, start, Math.Max(0, last - start));
        var modelAssetId = component.ModelAssetId;
        if (!ReferenceEquals(order.Model, model))
        {
            order.Model = model;
            order.ModelOrder = renderList.OrderOf(model);
        }
        for (var i = start; i < last; i++)
        {
            if (UseFrustumCulling && !GeometryUtility.TestPlanesAabb(frustumPlanes, cached.Bounds[i - start]))
            {
                CullingStats = CullingStats with { Culled = CullingStats.Culled + 1 };
                continue;
            }

            var slot = i - start;
            var overrideReference = slot < component.Materials.Count ? component.Materials[slot] : null;
            var material = ResolveDrawMaterial(ref order, modelAssetId, model.SubMeshes[i].MaterialIndex,
                overrideReference);
            var key = RenderList.SortKey(order.MaterialOrder, order.ModelOrder);
            renderList.Draws.Add(new DrawItem(model, i, material, cached.ModelMatrix, cached.NormalMatrix, key,
                renderList.Draws.Count, cached.Bounds[i - start], component.Node!.RenderLayer, component.Color,
                component.CustomData));
        }
    }

    // Consecutive draws of one model and material slot reuse the previous resolution within a BuildDraws call.
    MaterialResource ResolveDrawMaterial(ref DrawOrder order, Guid modelAssetId, int? materialIndex,
        AssetReference<MaterialAsset>? overrideReference)
    {
        if (order.Material is { } resolved && order.MaterialModelId == modelAssetId &&
            order.MaterialIndex == materialIndex && ReferenceEquals(order.MaterialOverride, overrideReference))
        {
            return resolved;
        }

        var material = ResolveMaterial(modelAssetId, materialIndex, overrideReference);
        if (!ReferenceEquals(order.Material, material))
        {
            order.Material = material;
            order.MaterialOrder = renderList.OrderOf(material);
        }

        order.MaterialModelId = modelAssetId;
        order.MaterialIndex = materialIndex;
        order.MaterialOverride = overrideReference;
        return material;
    }

    struct DrawOrder
    {
        public VulkanModel? Model;
        public int ModelOrder;
        public MaterialResource? Material;
        public int MaterialOrder;
        public Guid MaterialModelId;
        public int? MaterialIndex;
        public AssetReference<MaterialAsset>? MaterialOverride;
    }

    void RecordDraws(CommandBuffer commandBuffer, int frameIndex)
    {
        var draws = CollectionsMarshal.AsSpan(renderList.Draws);
        var state = new RecordState();
        if (!draws.IsEmpty)
        {
            // One instance per draw, at the draw's index: merged runs read consecutive instances, and an indirect
            // command's first instance is its candidate index.
            var instances = FrameInstances(frameIndex, draws.Length);
            WriteInstances(instances.Instances, draws);
            BindInstances(commandBuffer, instances.Set);
            RecordDrawItems(commandBuffer, draws, ref state);
        }

        RecordBatches(commandBuffer, ref state);
        FrameStats = CollectStatistics ? new RenderFrameStats
        {
            DrawCalls = state.Draws,
            Instances = state.Instances,
            Triangles = state.Triangles,
            MaterialBinds = state.MaterialBinds,
            Submeshes = CullingStats,
            Indirect = indirect,
        } : default;
    }

    void RecordDrawItems(CommandBuffer commandBuffer, ReadOnlySpan<DrawItem> draws, ref RecordState state)
    {
        for (var start = 0; start < draws.Length;)
        {
            ref readonly var draw = ref draws[start];
            var count = indirect ? 1 : RenderList.RunLength(draws, start);
            Bind(commandBuffer, ref state, draw.Model, draw.Material);
            if (indirect)
            {
                draw.Model.DrawSubMeshIndirect(commandBuffer, occlusion!.IndirectBuffer, (ulong)start * 20);
            }
            else
            {
                draw.Model.DrawSubMesh(commandBuffer, draw.SubMesh, (uint)count, (uint)start);
            }

            state.Count(draw.Model, draw.SubMesh, count);
            start += count;
        }
    }

    void RecordBatches(CommandBuffer commandBuffer, ref RecordState state)
    {
        foreach (var batch in batches.Visible)
        {
            // A batch whose upload was never recorded has nothing valid to draw yet.
            if (batch.Buffer is not { } buffer || batch.Count == 0)
            {
                continue;
            }

            BindInstances(commandBuffer, buffer.Set);
            var model = batch.Model;
            for (var i = 0; i < model.SubMeshes.Count; i++)
            {
                var material = ResolveMaterial(batch.Component.ModelAssetId, model.SubMeshes[i].MaterialIndex,
                    batch.Component.Material);
                Bind(commandBuffer, ref state, model, material);
                model.DrawSubMesh(commandBuffer, i, (uint)batch.Count, 0);
                state.Count(model, i, batch.Count);
            }
        }
    }

    void Bind(CommandBuffer commandBuffer, ref RecordState state, VulkanModel model, MaterialResource material)
    {
        if (!ReferenceEquals(material, state.Material))
        {
            BindMaterial(commandBuffer, material);
            state.Material = material;
            state.MaterialBinds++;
        }

        if (!ReferenceEquals(model, state.Model))
        {
            model.Bind(commandBuffer);
            state.Model = model;
        }
    }

    InstanceBuffer FrameInstances(int frameIndex, int count)
    {
        var slot = frameIndex % frameInstances.Length;
        var current = frameInstances[slot];
        if (current is not null && current.Capacity >= count)
        {
            return current;
        }

        retired.Retire(current);
        var capacity = InstanceBuffer.Grow(current?.Capacity ?? 0, count);
        return frameInstances[slot] = new InstanceBuffer(vulkan, instanceLayout, capacity, hostVisible: true);
    }

    static void WriteInstances(Span<InstanceData> destination, ReadOnlySpan<DrawItem> draws)
    {
        for (var i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            destination[i] = InstanceData.Create(draw.ModelMatrix, draw.NormalMatrix, draw.RenderLayer, draw.Color,
                draw.CustomData);
        }
    }

    unsafe void BindInstances(CommandBuffer commandBuffer, DescriptorSet set) =>
        vulkan.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 2, 1, in set, 0,
            null);

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
        if (reference is not { IsEmpty: false })
        {
            return null;
        }

        if (!overrideMaterials.TryGetValue(reference.AssetId, out var handle))
        {
            overrideMaterials[reference.AssetId] = handle = new MaterialAsset { Id = reference.AssetId };
        }

        return handle.GetContent(materials);
    }

    MaterialResource? DerivedMaterial(Guid modelAssetId, int? materialIndex)
    {
        if (modelAssetId == Guid.Empty || materialIndex is not { } index)
        {
            return null;
        }

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
            if (component.Node is not { } lightNode)
            {
                continue;
            }

            if (component.Type == LightType.Directional)
            {
                if (directionalIndex == ubo.DirectionalCount)
                {
                    continue;
                }

                ubo.SetDirectionalLight(
                    directionalIndex,
                    lightNode.GlobalTransform.Forward,
                    component.Color,
                    component.Intensity);
                ubo.SetDirectionalLightMask(directionalIndex++, component.CullingMask);
                continue;
            }

            if (pointIndex == ubo.Count)
            {
                continue;
            }

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
        batches.Dispose();
        foreach (var instances in frameInstances)
        {
            instances?.Dispose();
        }

        retired.Dispose();
        instanceLayout.Dispose();
        worldBounds.Clear();
        preparedBounds.Clear();
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
            instanceLayout.GetDescriptorSetLayout(),
        ];

        fixed (Silk.NET.Vulkan.DescriptorSetLayout* descriptorSetLayoutPtr = descriptorSetLayouts)
        {
            PipelineLayoutCreateInfo pipelineLayoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = descriptorSetLayoutPtr,
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

    /// <summary>The state bound while recording one frame, and the counts it reports.</summary>
    struct RecordState
    {
        public VulkanModel? Model;
        public MaterialResource? Material;
        public int Draws;
        public int Instances;
        public long Triangles;
        public int MaterialBinds;

        /// <summary>Counts one draw command of <paramref name="instances"/> instances of a submesh.</summary>
        public void Count(VulkanModel model, int subMesh, int instances)
        {
            Draws++;
            Instances += instances;
            Triangles += model.SubMeshes[subMesh].IndexCount / 3 * instances;
        }
    }
}
