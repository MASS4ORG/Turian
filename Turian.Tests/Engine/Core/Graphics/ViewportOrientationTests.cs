namespace Turian.Tests;

/// <summary>Checks world-up placement in the real pixel output used by Scene and Game views.</summary>
[Collection(SerialTests.Name)]
public sealed class ViewportOrientationTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Positive world Y appears above the center for both camera kinds and projections.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WorldUpRendersAboveCenter(bool gameCamera, bool orthographic)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = new Node();
        var quad = new Node { Position = new Vector3(0, 1, 0), Parent = root };
        quad.AddComponent(new ModelComponent { ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan) });
        root.Children.Add(quad);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 128, 128);
        viewer.ClearColor = new Vector4(0, 0, 0, 1);
        viewer.Camera.Position = new Vector3(0, 0, -5);
        viewer.Camera.IsOrthographic = orthographic;
        viewer.Camera.Frustum = 3;
        var camera = new CameraComponent { UsePerspective = !orthographic, Frustum = 3 };
        new Node { Position = new Vector3(0, 0, -5) }.AddComponent(camera);
        camera.Resize(128, 128);
        viewer.Render(root, 0.016, gameCamera ? camera : viewer.Camera);
        var pixels = new byte[128 * 128 * 4];
        viewer.CopyPixels(pixels);
        var rows = Enumerable.Range(0, 128).Where(row => Enumerable.Range(0, 128).Any(column =>
            pixels[(row * 128 + column) * 4] > 0)).ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(row < 64, $"Geometry at world Y=1 appeared on pixel row {row}"));
    }
}
