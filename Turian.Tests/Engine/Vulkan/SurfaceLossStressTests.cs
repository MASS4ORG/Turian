using Silk.NET.Windowing;

namespace Turian.Tests;

/// <summary>
/// Destroys and recreates a real window's surface and swap chain repeatedly. It opens a window, so it runs only on a
/// desktop with a GPU when <c>TURIAN_WINDOWED_TESTS=1</c>; add <c>VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation</c>
/// to check that the cycles are validation-clean.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class SurfaceLossStressTests
{
    const string optIn = "TURIAN_WINDOWED_TESTS";

    /// <summary>A hundred minimize and restore cycles release and rebuild the surface and keep rendering.</summary>
    [Fact]
    public void HundredMinimizeRestoreCyclesKeepRendering()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(optIn) == "1",
            $"opens a window; set {optIn}=1 on a desktop with a Vulkan GPU");

        using var host = new GlfwSurfaceHost();
        host.Initialize();
        var vulkan = new Vulkan(host, NullLogger.Instance);
        var renderer = new Renderer(vulkan.Vk, vulkan.Device, host, useFifo: false);
        try
        {
            renderer.Initialize();
            for (var i = 0; i < 100; i++)
            {
                RenderOne(renderer);
                host.OnStateChanged(WindowState.Minimized);
                Assert.True(renderer.IsSurfaceLost);
                Assert.False(vulkan.Device.HasSurface);
                Assert.Null(renderer.BeginFrame());

                host.OnStateChanged(WindowState.Normal);
                Assert.True(vulkan.Device.HasSurface);
            }

            RenderOne(renderer);
        }
        finally
        {
            renderer.Dispose();
            vulkan.Device.Dispose();
        }
    }

    // A swap chain rebuild may skip one frame; the next renders.
    static void RenderOne(Renderer renderer)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (renderer.BeginFrame() is not { } commandBuffer) continue;

            renderer.BeginSwapChainRenderPass(commandBuffer);
            renderer.EndSwapChainRenderPass(commandBuffer);
            renderer.EndFrame();
            return;
        }

        Assert.Fail("no frame rendered after three attempts");
    }
}
