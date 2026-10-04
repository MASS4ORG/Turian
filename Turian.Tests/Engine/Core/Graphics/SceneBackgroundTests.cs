namespace Turian.Tests;

/// <summary>Checks that the offscreen renderer clears empty pixels with the live background color.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneBackgroundTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Scene defaults are neutral dark gray, and subsequent color changes reach the rendered pixels.</summary>
    [Fact]
    public void EmptyPixelsUseConfiguredLinearColor()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 32, 32);
        viewer.ClearColor = new Vector4(new SceneViewSettings().EmptySkyColor, 1f);
        var scene = new Node();
        var pixels = new byte[32 * 32 * 4];
        viewer.Render(scene, 0.016);
        viewer.CopyPixels(pixels);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.InRange(pixels[0], (byte)40, (byte)65);
        Assert.Equal(255, pixels[3]);
        viewer.ClearColor = new Vector4(2f, -1f, 0.5f, 1f);
        viewer.Render(scene, 0.016);
        viewer.CopyPixels(pixels);
        Assert.Equal(255, pixels[2]);
        Assert.Equal(0, pixels[1]);
        Assert.InRange(pixels[0], (byte)185, (byte)190);
    }
}
