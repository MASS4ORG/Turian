namespace Turian.Engine.Core;

/// <summary>
/// Represents a renderer for rendering graphics using Vulkan.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="Renderer"/> class.
/// </remarks>
/// <param name="vk">The Vulkan context.</param>
/// <param name="device">The Vulkan device.</param>
/// <param name="surfaceHost">The host whose surface rendering presents to.</param>
/// <param name="useFifo">Specifies whether to use FIFO presentation mode.</param>
public partial class Renderer(Vk vk, Device device, ISurfaceHost surfaceHost, bool useFifo) : IDisposable
{
    // Swap chain and related data; there is no swap chain while the surface is lost.
    SwapChain? swapChain;
    CommandBuffer[] commandBuffers = [];
    SurfaceLifecycle lifecycle = null!;

    // Rendering-related state
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
    public SwapChain SwapChain => swapChain!;

    /// <summary>
    /// Gets the image format used by the swap chain.
    /// </summary>
    public Format SwapChainImageFormat => SwapChain.SwapChainImageFormat;

    /// <summary>
    /// Gets the depth format used by the swap chain.
    /// </summary>
    public Format SwapChainDepthFormat => SwapChain.SwapChainDepthFormat;

    /// <summary>
    /// Gets whether a rendering frame has been started.
    /// </summary>
    public bool IsFrameStarted => isFrameStarted;

    /// <summary>
    /// Gets the render pass associated with the swap chain.
    /// </summary>
    /// <returns>The render pass used by the swap chain.</returns>
    public RenderPass SwapChainRenderPass => SwapChain.RenderPass;

    /// <summary>
    /// Gets the aspect ratio of the swap chain's images.
    /// </summary>
    /// <returns>The aspect ratio of the swap chain images.</returns>
    public float AspectRatio => SwapChain.AspectRatio;

    /// <summary>Gets whether the host's surface is lost, during which every frame is skipped.</summary>
    public bool IsSurfaceLost => lifecycle?.IsSurfaceLost ?? false;

    /// <summary>
    /// Raised after a rebuilt swap chain changed formats, so pipelines built for the old render pass are rebuilt.
    /// </summary>
    public event Action? RenderPassChanged;

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
            if (swapChain is not null) swapChain.UseFifo = value;
        }
    }

    /// <summary>
    /// Initializes the renderer by following the host's surface events, creating the swap chain and command buffers.
    /// </summary>
    /// <exception cref="VulkanException">The surface has no drawable area to build the first swap chain for.</exception>
    public void Initialize()
    {
        lifecycle = new SurfaceLifecycle(surfaceHost, this);
        lifecycle.FormatChanged += OnFormatChanged;
        CreateCommandBuffers();
        if (!lifecycle.TryPrepare())
        {
            throw new VulkanException("Vulkan: the surface has no drawable area to create a swap chain for");
        }
    }

    /// <summary>
    /// Begins a new rendering frame. Acquires the next image for rendering.
    /// </summary>
    /// <returns>
    /// A <see cref="CommandBuffer"/> for recording rendering commands.
    /// Returns <c>null</c> while the surface is lost or empty, or when the swap chain had to be rebuilt.
    /// </returns>
    public CommandBuffer? BeginFrame()
    {
        Debug.Assert(!isFrameStarted, "Can't call beginFrame while already in progress!");

        if (!TryAcquire()) return null;

        isFrameStarted = true;

        var commandBuffer = CurrentCommandBuffer;
        BeginRecording(commandBuffer);
        return commandBuffer;
    }

    bool TryAcquire() => lifecycle.TryPrepare() && lifecycle.HandleResult(
        swapChain!.AcquireNextImage(ref currentImageIndex), "failed to acquire next swapchain image");

    void BeginRecording(CommandBuffer commandBuffer)
    {
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };

        var resultVulkan = vk.BeginCommandBuffer(commandBuffer, in beginInfo);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException($"Vulkan: failed to begin recording command buffer: {resultVulkan}");
        }
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

        var result = swapChain!.SubmitCommandBuffers(commandBuffer, currentImageIndex);
        _ = lifecycle.HandleResult(result, "failed to submit command buffers");

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
                RenderPass = SwapChain.RenderPass,
                Framebuffer = SwapChain.GetFrameBufferAt(currentImageIndex),
                RenderArea = { Offset = { X = 0, Y = 0 }, Extent = SwapChain.SwapChainExtent, }
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
                Width = SwapChain.SwapChainExtent.Width,
                Height = SwapChain.SwapChainExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f,
            };
        Rect2D scissor = new(new Offset2D(), SwapChain.SwapChainExtent);
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
    /// Disposes of the renderer's swap chain and command buffers and stops following the host's surface events.
    /// </summary>
    public void Dispose()
    {
        lifecycle?.Dispose();
        ISwapChainTarget target = this;
        target.WaitIdle();
        target.DestroySwapChain();
        FreeCommandBuffers();
        GC.SuppressFinalize(this);
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

        commandBuffers = [];
    }

    // One buffer per frame in flight, indexed by FrameIndex, so they outlive swap chain rebuilds.
    unsafe void CreateCommandBuffers()
    {
        commandBuffers = new CommandBuffer[SwapChain.MaxFramesInFlight];

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
