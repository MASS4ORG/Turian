using Turian.Engine.Hzb;

namespace Turian.Tests;

/// <summary>Checks the optional GPU visibility path against direct offscreen rendering.</summary>
[Collection(SerialTests.Name)]
public sealed class HzbCullingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    static Node Add(Node root, Model model, Vector3 position, Vector3 scale)
    {
        var node = new Node { Parent = root, Position = position, Scale = scale };
        node.AddComponent(new ModelComponent { ModelOverride = model });
        root.Children.Add(node);
        return node;
    }

    SceneViewerService Viewer(uint width = 128, uint height = 128) =>
        new(fixture.Vulkan, new AssetDatabase(), width, height)
        {
            OcclusionCullingFactory = new HzbCullingFactory()
        };

    static byte[] Pixels(SceneViewerService viewer, Node scene, bool enabled)
    {
        viewer.UseOcclusionCulling = enabled;
        viewer.Render(scene, 0.016);
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        return pixels;
    }

    /// <summary>Hidden submeshes have their indirect instance count cleared without changing any pixels.</summary>
    [Fact]
    public void OccludedDrawIsRejectedAndMovementIsImmediate()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var scene = new Node();
        var front = Add(scene, quad, new Vector3(0, 0, 4), new Vector3(8));
        Add(scene, quad, new Vector3(0, 0, 8), Vector3.One);
        using var viewer = Viewer();
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
        Pixels(viewer, scene, true);
        Assert.Equal(new RenderCullingStats(1, 1), viewer.OcclusionStats);
        Assert.True(viewer.OcclusionAllocatedBytes > 0);

        front.Position = new Vector3(1000, 0, 4);
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
        Pixels(viewer, scene, true);
        Assert.Equal(new RenderCullingStats(1, 0), viewer.OcclusionStats);
        viewer.Camera.Position = new Vector3(0, 0, 6);
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
    }

    /// <summary>Empty views allocate no visibility resources even when the optional pass is enabled.</summary>
    [Fact]
    public void EmptyViewAllocatesNothing()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = Viewer();
        Pixels(viewer, new Node(), true);
        Assert.Equal(0UL, viewer.OcclusionAllocatedBytes);
        Assert.Equal(default, viewer.OcclusionStats);
        Assert.Equal(default, viewer.CullingStats);
    }

    /// <summary>Clear pixels, odd viewport sizes, camera near-plane intersections and resized views retain geometry.</summary>
    [Fact]
    public void HolesAndNearPlaneRemainVisibleAfterResize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var scene = new Node();
        var front = Add(scene, quad, new Vector3(0, 0, 4), Vector3.One);
        var rear = Add(scene, quad, new Vector3(0, 0, 8), new Vector3(8));
        using var viewer = Viewer(127, 73);
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
        Pixels(viewer, scene, true);
        Assert.Equal(new RenderCullingStats(2, 0), viewer.OcclusionStats);
        viewer.Resize(83, 129);
        Assert.True(viewer.UseOcclusionCulling);
        front.Rotation = new Vector3(20, 45, 0);
        front.Scale = new Vector3(-2, 3, 1);
        rear.Position = new Vector3(0, 0, 0.15f);
        rear.Rotation = new Vector3(0, 70, 0);
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
    }

    /// <summary>Non-indexed models use the indirect command prefix and release their vertex buffer safely.</summary>
    [Fact]
    public void NonIndexedModelsUseIndirectCommands()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var triangle = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new(-1, -1, 0), Vector3.One), new(new(1, -1, 0), Vector3.One),
                new(new(0, 1, 0), Vector3.One)],
            Indices = [],
        });
        var scene = new Node();
        Add(scene, triangle, new Vector3(0, 0, 4), Vector3.One);
        using var viewer = Viewer();
        Assert.Equal(Pixels(viewer, scene, false), Pixels(viewer, scene, true));
    }

    /// <summary>Views keep independent depth resources and observe new draw candidates when their buffers grow.</summary>
    [Fact]
    public void MultipleViewsAndGrowingDrawListsRemainIndependent()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var scene = new Node();
        Add(scene, quad, new Vector3(0, 0, 4), new Vector3(8));
        using var first = Viewer();
        using var second = Viewer(63, 65);
        Pixels(first, scene, true);
        for (var i = 0; i < 70; i++) Add(scene, quad, new Vector3(0, 0, 8 + i), Vector3.One);
        Assert.Equal(Pixels(first, scene, false), Pixels(first, scene, true));
        Pixels(first, scene, true);
        Assert.Equal(new RenderCullingStats(1, 70), first.OcclusionStats);
        second.Camera.Position = new Vector3(0, 0, 5);
        Assert.Equal(Pixels(second, scene, false), Pixels(second, scene, true));
        Assert.Equal(Pixels(first, scene, false), Pixels(first, scene, true));
    }

    /// <summary>A provider retains separate frame slots and rebuilds its depth resources when the runtime size changes.</summary>
    [Fact]
    public unsafe void ProviderSupportsFrameSlotsAndTargetResize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var vulkan = fixture.Vulkan;
        using var layout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.UniformBuffer,
                ShaderStageFlags.VertexBit).Build();
        using var pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device).SetMaxSets(1)
            .AddPoolSize(DescriptorType.UniformBuffer, 1).Build();
        using var uniform = new Engine.Core.Buffer(vulkan, 128, 1,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        Assert.Equal(Result.Success, uniform.Map());
        DescriptorSet descriptor = default;
        Assert.True(new DescriptorSetWriter(vulkan.Vk, vulkan.Device, layout)
            .WriteBuffer(0, uniform.DescriptorInfo()).Build(pool, layout.GetDescriptorSetLayout(), ref descriptor));
        using var provider = new HzbCuller(vulkan, layout.GetDescriptorSetLayout());
        var quad = PreviewQuadMesh.Get(vulkan);
        var matrix = Matrix4x4.CreateTranslation(0, 0, 4);
        OcclusionDraw[] draws = [new(quad, 0, matrix, new Bounds(new Vector3(-0.5f, -0.5f, 4),
            new Vector3(0.5f, 0.5f, 4)))];
        var camera = new EditorCamera();
        for (var index = 0; index < 6; index++)
        {
            var width = index < 2 ? 128u : 83u;
            camera.Resize(width, 73);
            uniform.WriteToBuffer([camera.GetProjectionMatrix(), camera.GetViewMatrix()]);
            Submit(vulkan, command => Assert.True(provider.Record(new FrameInfo
            {
                FrameIndex = index % 2,
                CommandBuffer = command,
                GlobalDescriptorSet = descriptor,
                Camera = camera,
                ViewportWidth = width,
                ViewportHeight = 73,
            }, draws)));
        }
        Assert.Equal(new RenderCullingStats(1, 0), provider.CompletedStats);
        Assert.False(provider.Record(new FrameInfo(), []));
        Assert.False(provider.Record(new FrameInfo(), draws));
        provider.Dispose();
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => provider.Record(new FrameInfo(), draws));
    }

    static unsafe void Submit(Vulkan vulkan, Action<CommandBuffer> record)
    {
        CommandBufferAllocateInfo allocation = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = vulkan.Device.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        Assert.Equal(Result.Success,
            vulkan.Vk.AllocateCommandBuffers(vulkan.Device.VkDevice, in allocation, out var command));
        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Assert.Equal(Result.Success, vulkan.Vk.BeginCommandBuffer(command, in begin));
        record(command);
        Assert.Equal(Result.Success, vulkan.Vk.EndCommandBuffer(command));
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command
        };
        Assert.Equal(Result.Success,
            vulkan.Vk.QueueSubmit(vulkan.Device.GraphicsQueue, 1, in submit, default));
        Assert.Equal(Result.Success, vulkan.Vk.QueueWaitIdle(vulkan.Device.GraphicsQueue));
        vulkan.Vk.FreeCommandBuffers(vulkan.Device.VkDevice, vulkan.Device.CommandPool, 1, in command);
    }
}
