namespace Turian.Engine.Core;

public partial class Renderer : ISwapChainTarget
{
    bool ISwapChainTarget.HasSwapChain => swapChain is not null;

    bool ISwapChainTarget.SurfaceHasArea => HasArea(device.QuerySwapChainSupport.Capabilities.CurrentExtent);

    void ISwapChainTarget.WaitIdle() => _ = vk.DeviceWaitIdle(device.VkDevice);

    void ISwapChainTarget.CreateSurface() => device.RecreateSurface();

    void ISwapChainTarget.DestroySurface() => device.DestroySurface();

    SwapChainFormats ISwapChainTarget.CreateSwapChain(Extent2D extent)
    {
        var previous = swapChain;
        var created = new SwapChain(vk, device, extent, useFifo, previous);
        swapChain = created;
        previous?.Dispose();
        return new SwapChainFormats(created.SwapChainImageFormat, created.SwapChainDepthFormat);
    }

    void ISwapChainTarget.DestroySwapChain()
    {
        swapChain?.Dispose();
        swapChain = null;
    }

    // A minimized window can report a zero current extent even when its framebuffer size is not yet zero.
    static bool HasArea(Extent2D extent) => extent.Width != 0 && extent.Height != 0;

    void OnFormatChanged() => RenderPassChanged?.Invoke();
}
