namespace Turian.Engine.Core;

/// <summary>
/// Represents a renderer for rendering graphics using Vulkan.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="Renderer"/> class.
/// </remarks>
/// <param name="vk">The Vulkan context.</param>
/// <param name="device">The Vulkan device.</param>
/// <param name="window">The window where rendering will take place.</param>
/// <param name="useFifo">Specifies whether to use FIFO presentation mode.</param>
public class Renderer(Vk vk, Device device, IView window, bool useFifo) : IDisposable
{
    // Swap chain and related data
    SwapChain swapChain = null!;
    CommandBuffer[] commandBuffers = null!;

    // Rendering-related state
    bool framebufferResized;
    uint currentImageIndex;
    int currentFrameIndex;
    bool isFrameStarted;

    bool useFifo = useFifo;

    /// <summary>
    /// Gets the Vulkan swap chain used for rendering.
    /// </summary>
    /// <remarks>
    /// The swap chain represents the sequence of images that will be presented to the screen
    /// and is an essential part of the rendering process.
    /// </remarks>
    public SwapChain SwapChain => swapChain;

    /// <summary>
    /// Gets the image format used by the swap chain.
    /// </summary>
    public Format SwapChainImageFormat => swapChain.SwapChainImageFormat;

    /// <summary>
    /// Gets the depth format used by the swap chain.
    /// </summary>
    public Format SwapChainDepthFormat => swapChain.SwapChainDepthFormat;

    /// <summary>
    /// Gets whether a rendering frame has been started.
    /// </summary>
    public bool IsFrameStarted => isFrameStarted;

    /// <summary>
    /// Gets the render pass associated with the swap chain.
    /// </summary>
    /// <returns>The render pass used by the swap chain.</returns>
    public RenderPass SwapChainRenderPass => swapChain.RenderPass;

    /// <summary>
    /// Gets the aspect ratio of the swap chain's images.
    /// </summary>
    /// <returns>The aspect ratio of the swap chain images.</returns>
    public float AspectRatio => swapChain.AspectRatio;

    /// <summary>
    /// Gets the current command buffer for rendering.
    /// </summary>
    /// <returns>The current command buffer for rendering.</returns>
    public CommandBuffer CurrentCommandBuffer => commandBuffers[currentFrameIndex];

    /// <summary>
    /// Gets the index of the current rendering frame.
    /// </summary>
    /// <returns>The index of the current rendering frame.</returns>
    public int FrameIndex => currentFrameIndex;

    /// <summary>
    /// Gets or sets a value indicating whether FIFO presentation mode should be used.
    /// </summary>
    public bool UseFifo
    {
        get => useFifo;
        set
        {
            useFifo = value;
            swapChain.UseFifo = value;
        }
    }

    /// <summary>
    /// Initializes the renderer by recreating the swap chain and creating command buffers.
    /// </summary>
    public void Initialize()
    {
        RecreateSwapChain(true);
        CreateCommandBuffers();
    }

    /// <summary>
    /// Begins a new rendering frame. Acquires the next image for rendering.
    /// </summary>
    /// <returns>
    /// A <see cref="CommandBuffer"/> for recording rendering commands.
    /// Returns <c>null</c> if the swap chain needs to be recreated.
    /// </returns>
    public CommandBuffer? BeginFrame()
    {
        Debug.Assert(!isFrameStarted, "Can't call beginFrame while already in progress!");

        var result = swapChain.AcquireNextImage(ref currentImageIndex);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || framebufferResized)
        {
            framebufferResized = false;
            RecreateSwapChain();
            return null;
        }
        else if (result != Result.Success)
        {
            throw new VulkanException("failed to acquire next swapchain image");
        }

        isFrameStarted = true;

        var commandBuffer = CurrentCommandBuffer;

        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };

        var resultVulkan = vk.BeginCommandBuffer(commandBuffer, in beginInfo);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException($"Vulkan: failed to begin recording command buffer: {resultVulkan}");
        }

        return commandBuffer;
    }

    /// <summary>
    /// Ends the current rendering frame and submits the recorded commands to the swap chain.
    /// </summary>
    public void EndFrame()
    {
        Debug.Assert(isFrameStarted, "Can't call endFrame while frame is not in progress");

        var commandBuffer = CurrentCommandBuffer;

        var resultVulkan = vk.EndCommandBuffer(commandBuffer);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException($"Vulkan: failed to record command buffer: {resultVulkan}");
        }

        var result = swapChain.SubmitCommandBuffers(commandBuffer, currentImageIndex);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || framebufferResized)
        {
            framebufferResized = false;
            RecreateSwapChain();
        }
        else if (result != Result.Success)
        {
            throw new VulkanException("failed to submit command buffers");
        }

        // No DeviceWaitIdle here — SwapChain.SubmitCommandBuffers uses inFlightFences
        // and AcquireNextImage waits on them, so frames-in-flight pipelining is correct.
        // A full-device stall every frame would serialize CPU/GPU and defeat triple buffering.

        isFrameStarted = false;
        currentFrameIndex = (currentFrameIndex + 1) % SwapChain.MaxFramesInFlight;
    }

    /// <summary>
    /// Begins a render pass for the swap chain.
    /// </summary>
    /// <param name="commandBuffer">The command buffer for rendering.</param>
    public unsafe void BeginSwapChainRenderPass(CommandBuffer commandBuffer)
    {
        Debug.Assert(
            isFrameStarted,
            "Can't call beginSwapChainRenderPass if frame is not in progress"
        );
        Debug.Assert(
            commandBuffer.Handle == CurrentCommandBuffer.Handle,
            "Can't begin render pass on command buffer from a different frame"
        );

        RenderPassBeginInfo renderPassInfo =
            new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = swapChain.RenderPass,
                Framebuffer = swapChain.GetFrameBufferAt(currentImageIndex),
                RenderArea = { Offset = { X = 0, Y = 0 }, Extent = swapChain.SwapChainExtent, }
            };

        ClearValue[] clearValues =
        [
            new()
            {
                Color = new()
                {
                    Float32_0 = 0.01f,
                    Float32_1 = 0.01f,
                    Float32_2 = 0.01f,
                    Float32_3 = 1
                },
            },
            new()
            {
                DepthStencil = new() { Depth = 1, Stencil = 0 }
            }
        ];

        fixed (ClearValue* clearValuesPtr = clearValues)
        {
            renderPassInfo.ClearValueCount = (uint)clearValues.Length;
            renderPassInfo.PClearValues = clearValuesPtr;

            vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);
        }

        Viewport viewport =
            new()
            {
                X = 0.0f,
                Y = 0.0f,
                Width = swapChain.SwapChainExtent.Width,
                Height = swapChain.SwapChainExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f,
            };
        Rect2D scissor = new(new Offset2D(), swapChain.SwapChainExtent);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    /// <summary>
    /// Ends the current render pass for the swap chain.
    /// </summary>
    /// <param name="commandBuffer">The command buffer for rendering.</param>
    public void EndSwapChainRenderPass(CommandBuffer commandBuffer)
    {
        Debug.Assert(
            isFrameStarted,
            "Can't call endSwapChainRenderPass if frame is not in progress"
        );
        Debug.Assert(
            commandBuffer.Handle == CurrentCommandBuffer.Handle,
            "Can't end render pass on command buffer from a different frame"
        );

        vk.CmdEndRenderPass(commandBuffer);
    }

    /// <summary>
    /// Recreates the swap chain, handling changes in window size or format.
    /// </summary>
    public void RecreateSwapChain(bool forceCreateSwapChain = false)
    {
        _ = vk.DeviceWaitIdle(device.VkDevice); // Wait until the device is idle

        var frameBufferSize = window.FramebufferSize;
        while (frameBufferSize.X == 0 || frameBufferSize.Y == 0)
        {
            frameBufferSize = window.FramebufferSize;
            window.DoEvents();
        }

        if (forceCreateSwapChain)
        {
            swapChain = new SwapChain(vk, device, GetWindowExtents(), useFifo);
        }
        else
        {
            var oldImageFormat = swapChain.SwapChainImageFormat;
            var oldDepthFormat = swapChain.SwapChainDepthFormat;

            swapChain.Dispose();
            swapChain = new SwapChain(vk, device, GetWindowExtents(), useFifo);

            if (swapChain.SwapChainImageFormat != oldImageFormat || swapChain.SwapChainDepthFormat != oldDepthFormat)
            {
                throw new VulkanException("Swap chain image(or depth) format has changed!");
            }
        }
    }

    /// <summary>
    /// Disposes of the renderer and its resources.
    /// </summary>
    public void Dispose()
    {
        FreeCommandBuffers();
        GC.SuppressFinalize(this);
    }

    Extent2D GetWindowExtents()
    {
        return new Extent2D((uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
    }

    unsafe void FreeCommandBuffers()
    {
        fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
        {
            vk.FreeCommandBuffers(
                device.VkDevice,
                device.CommandPool,
                (uint)commandBuffers.Length,
                commandBuffersPtr
            );
        }

        Array.Clear(commandBuffers);
    }

    unsafe void CreateCommandBuffers()
    {
        commandBuffers = new CommandBuffer[swapChain.ImageCount()];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = device.CommandPool,
            CommandBufferCount = (uint)commandBuffers.Length,
        };

        fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
        {
            if (
                vk.AllocateCommandBuffers(device.VkDevice, in allocInfo, commandBuffersPtr)
                != Result.Success
            )
            {
                throw new VulkanException("failed to allocate command buffers!");
            }
        }
    }
}
