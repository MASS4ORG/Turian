namespace Turian.Engine.Core;

/// <summary>
/// Represents a Vulkan swap chain for rendering frames to a window or surface.
/// </summary>
public partial class SwapChain : IDisposable
{
    /// <summary>
    /// Gets the maximum frames in flight.
    /// </summary>
    public const int MaxFramesInFlight = 2;

    /// <summary>
    /// Gets the Vulkan swap chain.
    /// </summary>
    public SwapchainKHR VkSwapChain => swapChain;

    /// <summary>
    /// Gets the format of the swap chain images.
    /// </summary>
    public Format SwapChainImageFormat => swapChainImageFormat;

    /// <summary>
    /// Gets the format of the depth component in the swap chain.
    /// </summary>
    public Format SwapChainDepthFormat => swapChainDepthFormat;

    /// <summary>
    /// Gets an array of image views for the swap chain images.
    /// </summary>
    /// <returns>An array of image views.</returns>
    public ImageView[] GetSwapChainImageViews() => swapChainImageViews;

    /// <summary>
    /// Gets the framebuffer at a specified index for rendering.
    /// </summary>
    /// <param name="i">The index of the framebuffer to retrieve.</param>
    /// <returns>The framebuffer at the specified index.</returns>
    public Framebuffer GetFrameBufferAt(uint i) => swapChainFramebuffers[i];

    /// <summary>
    /// Gets an array of framebuffers for rendering.
    /// </summary>
    /// <returns>An array of framebuffers.</returns>
    public Framebuffer[] GetFrameBuffers() => swapChainFramebuffers;

    /// <summary>
    /// Gets the count of framebuffers in the swap chain.
    /// </summary>
    /// <returns>The count of framebuffers.</returns>
    public uint GetFrameBufferCount() => (uint)swapChainFramebuffers.Length;

    /// <summary>
    /// Indicates whether FIFO swapping is used.
    /// </summary>
    public bool UseFifo { get; set; }

    /// <summary>
    /// Gets the width of the swap chain.
    /// </summary>
    public uint Width => swapChainExtent.Width;

    /// <summary>
    /// Gets the height of the swap chain.
    /// </summary>
    public uint Height => swapChainExtent.Height;

    /// <summary>
    /// Gets the extent of the swap chain.
    /// </summary>
    /// <returns>The extent of the swap chain.</returns>
    public Extent2D SwapChainExtent => swapChainExtent;

    /// <summary>
    /// Gets the aspect ratio of the swap chain.
    /// </summary>
    /// <returns>The aspect ratio of the swap chain.</returns>
    public float AspectRatio => (float)swapChainExtent.Width / swapChainExtent.Height;

    /// <summary>
    /// Gets the render pass of the swap chain.
    /// </summary>
    /// <returns>The render pass of the swap chain.</returns>
    public RenderPass RenderPass => renderPass;

    readonly Vk vk;
    readonly Device device;
    readonly Silk.NET.Vulkan.Device vkDevice;
    KhrSwapchain khrSwapChain = null!;
    SwapchainKHR swapChain;
    Image[] swapChainImages = null!;
    Format swapChainDepthFormat;
    Extent2D swapChainExtent;
    ImageView[] swapChainImageViews = null!;
    Framebuffer[] swapChainFramebuffers = null!;
    RenderPass renderPass;
    Image[] depthImages = null!;
    DeviceMemory[] depthImageMemorys = null!;
    ImageView[] depthImageViews = null!;
    Image[] colorImages = null!;
    DeviceMemory[] colorImageMemorys = null!;
    ImageView[] colorImageViews = null!;
    Extent2D windowExtent;
    Silk.NET.Vulkan.Semaphore[] imageAvailableSemaphores = null!;
    Silk.NET.Vulkan.Semaphore[] renderFinishedSemaphores = null!;
    Fence[] inFlightFences = null!;
    Fence[] imagesInFlight = null!;
    int currentFrame;
    Format swapChainImageFormat;
    readonly SwapChain? oldSwapChain = null!;

    /// <summary>
    /// Gets the image count of the swap chain.
    /// </summary>
    /// <returns>The image count of the swap chain.</returns>
    public uint ImageCount() => (uint)swapChainImageViews.Length;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwapChain"/> class.
    /// </summary>
    /// <param name="vk">The Vulkan API object.</param>
    /// <param name="device">The device object.</param>
    /// <param name="extent">The extent of the swap chain.</param>
    /// <param name="useFifo">Whether to use Fifo for the swap chain.</param>
    public SwapChain(Vk vk, Device device, Extent2D extent, bool useFifo)
    {
        ArgumentNullException.ThrowIfNull(device);

        this.vk = vk;
        this.device = device;
        UseFifo = useFifo;
        vkDevice = device.VkDevice;
        windowExtent = extent;
        Initialize();
    }

    /// <summary>
    /// Acquires the next image in the swap chain.
    /// </summary>
    /// <param name="imageIndex">The index of the image to acquire.</param>
    /// <returns>The result of the acquire operation.</returns>
    public Result AcquireNextImage(ref uint imageIndex)
    {
        _ = vk.WaitForFences(device.VkDevice, 1, in inFlightFences[currentFrame], true, ulong.MaxValue);

        var result = khrSwapChain.AcquireNextImage(
            device.VkDevice,
            swapChain,
            ulong.MaxValue,
            imageAvailableSemaphores[currentFrame],
            default,
            ref imageIndex
        );

        return result;
    }

    /// <summary>
    /// Submits the command buffers for execution.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to submit.</param>
    /// <param name="imageIndex">The index of the image in the swap chain.</param>
    /// <returns>The result of the submission.</returns>
    /// <remarks>
    /// This method performs several tasks:
    /// - Waits for the fences before acquiring the next image.
    /// - Assigns in-flight images.
    /// - Configures and submits the submit info.
    /// - Resets the fences.
    /// - Handles the presentation of the queue.
    /// This method is unsafe because it uses pointers and unmanaged memory operations.
    /// </remarks>
    public unsafe Result SubmitCommandBuffers(CommandBuffer commandBuffer, uint imageIndex)
    {
        if (imagesInFlight[imageIndex].Handle != default)
        {
            _ = vk.WaitForFences(device.VkDevice, 1, in imagesInFlight[imageIndex], true, ulong.MaxValue);
        }

        imagesInFlight[imageIndex] = inFlightFences[currentFrame];

        SubmitInfo submitInfo = new() { SType = StructureType.SubmitInfo, };

        var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
        var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };

        submitInfo = submitInfo with
        {
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSemaphores,
            PWaitDstStageMask = waitStages,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        var signalSemaphores = stackalloc[] { renderFinishedSemaphores[currentFrame] };
        submitInfo = submitInfo with
        {
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSemaphores,
        };

        _ = vk.ResetFences(device.VkDevice, 1, in inFlightFences[currentFrame]);

        if (vk.QueueSubmit(device.GraphicsQueue, 1, in submitInfo, inFlightFences[currentFrame]) != Result.Success
           )
        {
            throw new VulkanException("failed to submit draw command buffer!");
        }

        var swapChains = stackalloc[] { swapChain };
        PresentInfoKHR presentInfo =
            new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = signalSemaphores,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &imageIndex
            };

        var result = khrSwapChain.QueuePresent(device.PresentQueue, in presentInfo);

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;

        return result;
    }

    /// <summary>
    /// Dispose buffers
    /// </summary>
    public unsafe void Dispose()
    {
        foreach (var framebuffer in swapChainFramebuffers)
        {
            vk.DestroyFramebuffer(device.VkDevice, framebuffer, null);
        }

        foreach (var imageView in swapChainImageViews)
        {
            vk.DestroyImageView(device.VkDevice, imageView, null);
        }

        Array.Clear(swapChainImageViews);

        for (var i = 0; i < depthImages.Length; i++)
        {
            vk.DestroyImageView(device.VkDevice, depthImageViews[i], null);
            vk.DestroyImage(device.VkDevice, depthImages[i], null);
            vk.FreeMemory(device.VkDevice, depthImageMemorys[i], null);
        }

        for (var i = 0; i < colorImages.Length; i++)
        {
            vk.DestroyImageView(device.VkDevice, colorImageViews[i], null);
            vk.DestroyImage(device.VkDevice, colorImages[i], null);
            vk.FreeMemory(device.VkDevice, colorImageMemorys[i], null);
        }

        vk.DestroyRenderPass(device.VkDevice, renderPass, null);

        // cleanup synchronization objects
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            vk.DestroySemaphore(device.VkDevice, renderFinishedSemaphores[i], null);
            vk.DestroySemaphore(device.VkDevice, imageAvailableSemaphores[i], null);
            vk.DestroyFence(device.VkDevice, inFlightFences[i], null);
        }

        khrSwapChain.DestroySwapchain(device.VkDevice, swapChain, null);
        swapChain = default;

        GC.SuppressFinalize(this);
    }

}
