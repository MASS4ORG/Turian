namespace Turian.Tests;

/// <summary>Checks the camera background encoded into Vulkan render-pass clear commands without a window.</summary>
public sealed class RendererBackgroundTests
{
    /// <summary>The camera's authored linear RGBA channels reach the Vulkan clear value.</summary>
    [Fact]
    public void CameraBackgroundReachesTheVulkanClearValue()
    {
        var camera = new CameraComponent { BackgroundColor = new Color(0.25f, 0.5f, 0.75f, 0.8f) };

        var clear = Renderer.ClearColor(RendererManager.CameraBackground(camera));

        Assert.Equal(0.25f, clear.Float32_0);
        Assert.Equal(0.5f, clear.Float32_1);
        Assert.Equal(0.75f, clear.Float32_2);
        Assert.Equal(0.8f, clear.Float32_3);
    }

    /// <summary>A camera without a background uses the runtime's opaque near-black fallback.</summary>
    [Fact]
    public void CamerasWithoutBackgroundsKeepTheRuntimeFallback()
    {
        var background = RendererManager.CameraBackground(Substitute.For<ICamera>());
        Assert.Null(background);

        var clear = Renderer.ClearColor(background);

        Assert.Equal(0.01f, clear.Float32_0);
        Assert.Equal(0.01f, clear.Float32_1);
        Assert.Equal(0.01f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    /// <summary>Out-of-range color and alpha channels are clamped before reaching Vulkan.</summary>
    [Fact]
    public void ClearChannelsStayWithinTheNormalizedRange()
    {
        var clear = Renderer.ClearColor(new Vector4(-1f, 2f, 0.5f, 3f));

        Assert.Equal(0f, clear.Float32_0);
        Assert.Equal(1f, clear.Float32_1);
        Assert.Equal(0.5f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }
}
