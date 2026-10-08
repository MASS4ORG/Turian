namespace Turian.Tests;

/// <summary>Verifies submitted counters against known geometry through the real offscreen renderer.</summary>
[Collection(SerialTests.Name)]
public sealed class FrameStatisticsRenderingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Visible draws and shared material binds reset each frame and survive a resize.</summary>
    [Fact]
    public void CountersMatchSubmittedGeometryAndDisableCleanly()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 32, 32);
        var root = new Node();
        var model = PreviewQuadMesh.Get(fixture.Vulkan);
        foreach (var x in new[] { 0f, 0f, 1000f })
        {
            var node = new Node { Position = new Vector3(x, 0, 4), Parent = root };
            node.AddComponent(new ModelComponent { ModelOverride = model });
            root.Children.Add(node);
        }
        viewer.Render(root, 0.05);
        Assert.Equal(default, viewer.FrameStats);
        viewer.CollectStatistics = true;
        viewer.Render(root, 0.05);
        Assert.Equal(2, viewer.FrameStats.DrawCalls);
        Assert.Equal(4, viewer.FrameStats.Triangles);
        Assert.Equal(1, viewer.FrameStats.MaterialBinds);
        Assert.Equal(new RenderCullingStats(2, 1), viewer.FrameStats.Submeshes);
        Assert.False(viewer.FrameStats.Indirect);
        Assert.True(viewer.FrameStats.CpuMilliseconds > 0);
        Assert.True(viewer.FrameStats.PrepareMilliseconds > 0);
        Assert.True(viewer.FrameStats.SubmitMilliseconds > 0);
        Assert.True(viewer.FrameStats.AllocatedBytes >= 0);
        viewer.Resize(64, 32);
        Assert.True(viewer.CollectStatistics);
        viewer.UseFrustumCulling = false;
        viewer.Render(root, 0.05);
        Assert.Equal(3, viewer.FrameStats.DrawCalls);
        Assert.Equal(6, viewer.FrameStats.Triangles);
        viewer.Render(new Node(), 0.05);
        Assert.Equal(0, viewer.FrameStats.DrawCalls);
        Assert.Equal(0, viewer.FrameStats.Triangles);
        Assert.Equal(0, viewer.FrameStats.MaterialBinds);
        viewer.CollectStatistics = false;
        viewer.Render(root, 0.05);
        Assert.Equal(default, viewer.FrameStats);
    }

    /// <summary>A triangle without an index buffer contributes one draw and one submitted primitive.</summary>
    [Fact]
    public void NonIndexedTrianglesAreCounted()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var model = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new(-1, -1, 0), Vector3.One), new(new(1, -1, 0), Vector3.One),
                new(new(0, 1, 0), Vector3.One)],
            SubMeshes = [new SubMesh(0, 3)],
        });
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 4), Parent = root };
        node.AddComponent(new ModelComponent { ModelOverride = model });
        root.Children.Add(node);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 32, 32)
        {
            CollectStatistics = true,
        };
        viewer.Render(root, 0.05);
        Assert.Equal(1, viewer.FrameStats.DrawCalls);
        Assert.Equal(1, viewer.FrameStats.Triangles);
    }
}
