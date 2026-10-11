namespace Turian.Engine.Core;

/// <summary>
/// Renders filled triangles and lines after scene geometry, using separate world and overlay depth settings.
/// Lines expand into screen-space quads of the requested pixel thickness; filled triangles retain world projection.
/// </summary>
public sealed class GizmoRenderSystem : IRenderSystem
{
    const string vertShaderPath = "gizmoShader.vert.spv";
    const string fragShaderPath = "gizmoShader.frag.spv";
    const string rendererName = "GizmoRenderer";
    const int verticesPerLine = 6;
    const int verticesPerTriangle = 3;
    const int maxVertices = Gizmos.MaxLines * verticesPerLine + Gizmos.MaxTriangles * verticesPerTriangle;
    const int initialCapacity = 1_024;
    const float worldDepthOffset = -0.0005f;

    readonly Vulkan vulkan;
    readonly Gizmos gizmos;

    PipelineLayout pipelineLayout;
    StandardPipeline worldPipeline = null!;
    StandardPipeline overlayPipeline = null!;
    Buffer vertexBuffer = null!;
    GizmoExpandedVertex[] scratch = [];
    int capacity;

    /// <summary>
    /// Creates a new gizmo render system bound to <paramref name="renderPass"/>.
    /// </summary>
    /// <param name="vulkan">The shared headless Vulkan context.</param>
    /// <param name="renderPass">The render pass to draw inside.</param>
    /// <param name="globalSetLayout">The global descriptor set layout (set 0).</param>
    /// <param name="gizmos">The geometry source to draw every frame.</param>
    public GizmoRenderSystem(
        Vulkan vulkan,
        RenderPass renderPass,
        Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout,
        Gizmos gizmos)
    {
        this.vulkan = vulkan;
        this.gizmos = gizmos;
        CreatePipelineLayout(globalSetLayout);
        CreatePipelines(renderPass);
        EnsureCapacity(initialCapacity);
    }

    /// <inheritdoc/>
    public unsafe void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        var worldVertexCount = gizmos.WorldLines.Count * verticesPerLine
            + gizmos.WorldTriangles.Count * verticesPerTriangle;
        var overlayVertexCount = gizmos.OverlayLines.Count * verticesPerLine
            + gizmos.OverlayTriangles.Count * verticesPerTriangle;
        var totalVertexCount = worldVertexCount + overlayVertexCount;
        if (totalVertexCount == 0) return;

        EnsureCapacity(totalVertexCount);
        UploadVertices(frameInfo.Camera);

        Silk.NET.Vulkan.Buffer[] vertexBuffers = [vertexBuffer.VkBuffer];
        ulong[] offsets = [0];
        fixed (ulong* offsetsPtr = offsets)
        fixed (Silk.NET.Vulkan.Buffer* vertexBuffersPtr = vertexBuffers)
        {
            vulkan.Vk.CmdBindVertexBuffers(frameInfo.CommandBuffer, 0, 1, vertexBuffersPtr, offsetsPtr);
        }

        var descriptorSet = frameInfo.GlobalDescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(
            frameInfo.CommandBuffer,
            PipelineBindPoint.Graphics,
            pipelineLayout,
            0,
            1,
            in descriptorSet,
            0,
            null);

        var viewport = new Vector2(frameInfo.ViewportWidth, frameInfo.ViewportHeight);

        if (worldVertexCount > 0)
        {
            worldPipeline.Bind(frameInfo.CommandBuffer);
            PushConstants(frameInfo.CommandBuffer, viewport, worldDepthOffset);
            vulkan.Vk.CmdDraw(frameInfo.CommandBuffer, (uint)worldVertexCount, 1, 0, 0);
        }

        if (overlayVertexCount > 0)
        {
            overlayPipeline.Bind(frameInfo.CommandBuffer);
            PushConstants(frameInfo.CommandBuffer, viewport, 0f);
            vulkan.Vk.CmdDraw(
                frameInfo.CommandBuffer,
                (uint)overlayVertexCount,
                1,
                (uint)worldVertexCount,
                0);
        }
    }

    void UploadVertices(ICamera camera)
    {
        var cursor = 0;
        AppendTriangles(gizmos.WorldTriangles, camera, ref cursor);
        AppendLines(gizmos.WorldLines, ref cursor);
        AppendTriangles(gizmos.OverlayTriangles, camera, ref cursor);
        AppendLines(gizmos.OverlayLines, ref cursor);
        vertexBuffer.WriteToIndex(scratch, 0);
    }

    void AppendLines(IReadOnlyList<GizmoLine> lines, ref int cursor)
    {
        foreach (var line in lines)
        {
            ExpandLine(scratch.AsSpan(cursor, verticesPerLine), in line);
            cursor += verticesPerLine;
        }
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        worldPipeline.Dispose();
        overlayPipeline.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        vertexBuffer.Dispose();
    }

    internal static void ExpandLine(Span<GizmoExpandedVertex> target, in GizmoLine line)
    {
        Span<GizmoExpandedVertex> quad =
        [
            new(line.A, line.B, line.Color, line.Thickness, -1f, -1f),
            new(line.A, line.B, line.Color, line.Thickness, -1f, +1f),
            new(line.A, line.B, line.Color, line.Thickness, +1f, +1f),
            new(line.A, line.B, line.Color, line.Thickness, -1f, -1f),
            new(line.A, line.B, line.Color, line.Thickness, +1f, +1f),
            new(line.A, line.B, line.Color, line.Thickness, +1f, -1f),
        ];
        quad.CopyTo(target);
    }

    void AppendTriangles(IReadOnlyList<GizmoTriangle> triangles, ICamera camera, ref int cursor)
    {
        if (triangles.Count == 0) return;
        var sorted = triangles.ToArray();
        Array.Sort(sorted, (a, b) => Depth(b).CompareTo(Depth(a)));
        foreach (var triangle in sorted)
        {
            scratch[cursor++] = SolidVertex(triangle.A, triangle.Color);
            scratch[cursor++] = SolidVertex(triangle.B, triangle.Color);
            scratch[cursor++] = SolidVertex(triangle.C, triangle.Color);
        }

        float Depth(GizmoTriangle triangle) =>
            Vector3.Dot((triangle.A + triangle.B + triangle.C) / 3f - camera.Position, camera.Front);
    }

    static GizmoExpandedVertex SolidVertex(Vector3 position, Vector4 color) =>
        new(position, position, color, 0f, 0f, 0f);

    void EnsureCapacity(int requiredVertexCount)
    {
        if (requiredVertexCount <= capacity) return;

        var newCapacity = Math.Max(initialCapacity, Math.Max(capacity * 2, requiredVertexCount));
        if (newCapacity > maxVertices)
        {
            newCapacity = maxVertices;
        }

        ScopedBufferRecreate(newCapacity);
        scratch = new GizmoExpandedVertex[newCapacity];
        capacity = newCapacity;
    }

    void ScopedBufferRecreate(int newCapacity)
    {
        if (capacity > 0)
        {
            vertexBuffer.Dispose();
        }

        vertexBuffer = new Buffer(
            vulkan,
            (uint)GizmoExpandedVertex.SizeOf(),
            (uint)newCapacity,
            BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = vertexBuffer.Map();
    }

    void PushConstants(CommandBuffer commandBuffer, Vector2 viewport, float depthOffset)
    {
        var push = new GizmoPushConstantData(viewport, depthOffset);
        vulkan.Vk.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.VertexBit,
            0,
            GizmoPushConstantData.SizeOf(),
            ref push);
    }

    unsafe void CreatePipelineLayout(Silk.NET.Vulkan.DescriptorSetLayout globalSetLayout)
    {
        Silk.NET.Vulkan.DescriptorSetLayout[] descriptorSetLayouts = [globalSetLayout];
        PushConstantRange pushConstantRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = GizmoPushConstantData.SizeOf(),
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

            var resultVulkan = vulkan.Vk.CreatePipelineLayout(
                vulkan.Device.VkDevice, in pipelineLayoutInfo, null, out pipelineLayout);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create gizmo pipeline layout: {0}", resultVulkan);
            }
        }
    }

    void CreatePipelines(RenderPass renderPass)
    {
        worldPipeline = CreatePipeline(renderPass, depthTest: true, depthWrite: false);
        overlayPipeline = CreatePipeline(renderPass, depthTest: false, depthWrite: false);
    }

    StandardPipeline CreatePipeline(RenderPass renderPass, bool depthTest, bool depthWrite)
    {
        var pipelineConfig = new PipelineConfigInfo();
        StandardPipeline.DefaultPipelineConfigInfo(ref pipelineConfig);
        StandardPipeline.EnableAlphaBlending(ref pipelineConfig);
        StandardPipeline.EnableMultiSampling(ref pipelineConfig, vulkan.Device.MsaaSamples);

        pipelineConfig.BindingDescriptions = GizmoBindingDescriptions;
        pipelineConfig.AttributeDescriptions = GizmoAttributeDescriptions;
        var rasterization = pipelineConfig.RasterizationInfo;
        rasterization.CullMode = CullModeFlags.None;
        pipelineConfig.RasterizationInfo = rasterization;

        var depthStencilInfo = pipelineConfig.DepthStencilInfo;
        depthStencilInfo.DepthTestEnable = depthTest ? Vk.True : Vk.False;
        depthStencilInfo.DepthWriteEnable = depthWrite ? Vk.True : Vk.False;
        pipelineConfig.DepthStencilInfo = depthStencilInfo;

        pipelineConfig.RenderPass = renderPass;
        pipelineConfig.PipelineLayout = pipelineLayout;
        return new StandardPipeline(
            vulkan.Vk,
            vulkan.Device,
            vertShaderPath,
            fragShaderPath,
            pipelineConfig,
            rendererName);
    }

    static readonly VertexInputBindingDescription[] GizmoBindingDescriptions =
    [
        new()
        {
            Binding = 0,
            Stride = (uint)GizmoExpandedVertex.SizeOf(),
            InputRate = VertexInputRate.Vertex,
        },
    ];

    static readonly VertexInputAttributeDescription[] GizmoAttributeDescriptions =
    [
        new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
        new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
        new() { Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 24 },
        new() { Location = 3, Binding = 0, Format = Format.R32Sfloat, Offset = 40 },
        new() { Location = 4, Binding = 0, Format = Format.R32Sfloat, Offset = 44 },
        new() { Location = 5, Binding = 0, Format = Format.R32Sfloat, Offset = 48 },
    ];
}
