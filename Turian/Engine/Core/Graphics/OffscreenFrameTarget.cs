namespace Turian.Engine.Core;

/// <summary>
/// Per-viewer offscreen render target. Mirrors SwapChain's attachment structure (MSAA color + depth +
/// resolve) so existing render-system pipelines are compatible. The resolved image is blitted to a
/// host-visible staging buffer each frame for CPU readback into an Avalonia WriteableBitmap.
/// One instance per scene-viewer panel; all instances share the same <see cref="Vulkan"/> device.
/// </summary>
public unsafe partial class OffscreenFrameTarget : IDisposable
{
    /// <summary>Color format used for the offscreen images. Matches Avalonia's Bgra8888 pixel format.</summary>
    public const Format ColorFormat = Format.B8G8R8A8Unorm;

    /// <summary>Gets the render pass. Compatible with any pipeline built for the same format + MSAA sample count.</summary>
    public RenderPass RenderPass => renderPass;

    /// <summary>Gets the current render width in pixels.</summary>
    public uint Width { get; private set; }

    /// <summary>Gets the current render height in pixels.</summary>
    public uint Height { get; private set; }

    readonly Vk vk;
    readonly Device device;

    // MSAA + resolve color images
    Image msaaColorImage;
    DeviceMemory msaaColorMemory;
    ImageView msaaColorView;

    Image resolveColorImage;
    DeviceMemory resolveColorMemory;
    ImageView resolveColorView;

    // Depth image
    Image depthImage;
    DeviceMemory depthMemory;
    ImageView depthView;

    Framebuffer framebuffer;
    RenderPass renderPass;

    // Single command buffer + sync (we wait-idle after each frame, so no double-buffering needed)
    CommandBuffer commandBuffer;
    Fence fence;

    // Host-visible staging buffer for readback
    Silk.NET.Vulkan.Buffer stagingBuffer;
    DeviceMemory stagingMemory;
    ulong stagingSize;

    bool isFrameStarted;

    /// <summary>
    /// Creates a new offscreen frame target.
    /// </summary>
    public OffscreenFrameTarget(Vk vk, Device device, uint width, uint height)
    {
        this.vk = vk;
        this.device = device;
        Width = width;
        Height = height;
        Initialize();
    }

    /// <summary>
    /// Begins a frame. Returns the command buffer to record rendering commands into,
    /// or <c>null</c> if the target has zero size.
    /// </summary>
    public CommandBuffer? BeginFrame()
    {
        if (Width == 0 || Height == 0) return null;
        Debug.Assert(!isFrameStarted);

        var waitResult = vk.WaitForFences(device.VkDevice, 1, in fence, true, 1_000_000_000UL);
        if (waitResult == Result.Timeout)
            throw new VulkanException("OffscreenFrameTarget.BeginFrame: WaitForFences timed out (likely GPU hang)");
        _ = vk.ResetFences(device.VkDevice, 1, in fence);

        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        var result = vk.BeginCommandBuffer(commandBuffer, in beginInfo);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to begin command buffer: {0}", result);

        isFrameStarted = true;
        return commandBuffer;
    }

    /// <summary>Begins the render pass on the given command buffer.</summary>
    public void BeginRenderPass(CommandBuffer cmd)
    {
        Debug.Assert(isFrameStarted);

        ClearValue[] clearValues =
        [
            new() { Color = new() { Float32_0 = 0.39f, Float32_1 = 0.58f, Float32_2 = 0.93f, Float32_3 = 1f } }, // cornflower blue — visible debug clear
            // new() { Color = new() { Float32_0 = 0.01f, Float32_1 = 0.01f, Float32_2 = 0.01f, Float32_3 = 1f } },
            new() { DepthStencil = new() { Depth = 1f, Stencil = 0 } }
        ];

        fixed (ClearValue* cvPtr = clearValues)
        {
            RenderPassBeginInfo rpInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = { Offset = new Offset2D(0, 0), Extent = new Extent2D(Width, Height) },
                ClearValueCount = (uint)clearValues.Length,
                PClearValues = cvPtr
            };
            vk.CmdBeginRenderPass(cmd, &rpInfo, SubpassContents.Inline);
        }

        Viewport viewport = new() { X = 0, Y = 0, Width = Width, Height = Height, MinDepth = 0f, MaxDepth = 1f };
        Rect2D scissor = new(new Offset2D(0, 0), new Extent2D(Width, Height));
        vk.CmdSetViewport(cmd, 0, 1, &viewport);
        vk.CmdSetScissor(cmd, 0, 1, &scissor);
    }

    /// <summary>Ends the render pass.</summary>
    public void EndRenderPass(CommandBuffer cmd)
    {
        Debug.Assert(isFrameStarted);
        vk.CmdEndRenderPass(cmd);
    }

    /// <summary>
    /// Ends the frame: copies the resolved color image into the staging buffer, submits,
    /// and waits for completion so the staging buffer is ready for <see cref="CopyToSpan"/>.
    /// </summary>
    public void EndFrame()
    {
        Debug.Assert(isFrameStarted);

        // Transition resolve image: TransferSrcOptimal → so we can blit to staging buffer
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.ColorAttachmentOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = resolveColorImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit
        };

        vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &barrier);

        // Copy resolve image → staging buffer
        BufferImageCopy region = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, 1)
        };
        vk.CmdCopyImageToBuffer(commandBuffer, resolveColorImage, ImageLayout.TransferSrcOptimal, stagingBuffer, 1, &region);

        _ = vk.EndCommandBuffer(commandBuffer);

        var cb = commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb
        };

        var result = vk.QueueSubmit(device.GraphicsQueue, 1, in submitInfo, fence);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: queue submit failed: {0}", result);

        var endWait = vk.WaitForFences(device.VkDevice, 1, in fence, true, 1_000_000_000UL);
        if (endWait == Result.Timeout)
            throw new VulkanException("OffscreenFrameTarget.EndFrame: GPU did not signal fence within 1s — likely GPU hang");

        isFrameStarted = false;
    }

    /// <summary>
    /// Copies the last rendered frame's pixels (BGRA8) into the given span.
    /// The span must be at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public void CopyToSpan(Span<byte> destination)
    {
        void* mapped;
        _ = vk.MapMemory(device.VkDevice, stagingMemory, 0, stagingSize, 0, &mapped);
        new Span<byte>(mapped, (int)stagingSize).CopyTo(destination);
        vk.UnmapMemory(device.VkDevice, stagingMemory);
    }

    /// <summary>Resizes the target. Disposes and recreates all Vulkan resources.</summary>
    public void Resize(uint newWidth, uint newHeight)
    {
        _ = vk.DeviceWaitIdle(device.VkDevice);
        DestroyResources();
        Width = newWidth;
        Height = newHeight;
        if (Width > 0 && Height > 0) Initialize();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ = vk.DeviceWaitIdle(device.VkDevice);
        DestroyResources();
        GC.SuppressFinalize(this);
    }

    void Initialize()
    {
        CreateRenderPass();
        CreateImages();
        CreateFramebuffer();
        CreateCommandBuffer();
        CreateStagingBuffer();
    }

    void CreateRenderPass()
    {
        var depthFormat = device.FindDepthFormat();
        var samples = device.MsaaSamples;

        AttachmentDescription colorAttachment = new()
        {
            Format = ColorFormat,
            Samples = samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        };

        AttachmentDescription depthAttachment = new()
        {
            Format = depthFormat,
            Samples = samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        // Resolve attachment: where the MSAA image is resolved; TransferSrcOptimal so we can copy to staging
        AttachmentDescription resolveAttachment = new()
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal  // we transition manually in EndFrame
        };

        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        AttachmentReference depthRef = new() { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        AttachmentReference resolveRef = new() { Attachment = 2, Layout = ImageLayout.ColorAttachmentOptimal };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
            PResolveAttachments = &resolveRef
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };

        AttachmentDescription[] attachments = [colorAttachment, depthAttachment, resolveAttachment];

        fixed (AttachmentDescription* attachPtr = attachments)
        {
            RenderPassCreateInfo rpInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = attachPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency
            };

            var result = vk.CreateRenderPass(device.VkDevice, in rpInfo, null, out renderPass);
            if (result != Result.Success)
                throw new VulkanException("OffscreenFrameTarget: failed to create render pass: {0}", result);
        }
    }

    void CreateImages()
    {
        var samples = device.MsaaSamples;
        var depthFormat = device.FindDepthFormat();

        // MSAA color image
        CreateImage(Width, Height, ColorFormat, samples,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out msaaColorImage, out msaaColorMemory);
        msaaColorView = CreateImageView(msaaColorImage, ColorFormat, ImageAspectFlags.ColorBit);

        // Resolve (1-sample) color image — will be copied to staging in EndFrame
        CreateImage(Width, Height, ColorFormat, SampleCountFlags.Count1Bit,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out resolveColorImage, out resolveColorMemory);
        resolveColorView = CreateImageView(resolveColorImage, ColorFormat, ImageAspectFlags.ColorBit);

        // Depth image
        CreateImage(Width, Height, depthFormat, samples,
            ImageUsageFlags.DepthStencilAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out depthImage, out depthMemory);
        depthView = CreateImageView(depthImage, depthFormat, ImageAspectFlags.DepthBit);
    }

    void CreateFramebuffer()
    {
        ImageView[] attachments = [msaaColorView, depthView, resolveColorView];

        fixed (ImageView* attachPtr = attachments)
        {
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = attachPtr,
                Width = Width,
                Height = Height,
                Layers = 1
            };

            var result = vk.CreateFramebuffer(device.VkDevice, in fbInfo, null, out framebuffer);
            if (result != Result.Success)
                throw new VulkanException("OffscreenFrameTarget: failed to create framebuffer: {0}", result);
        }
    }

    void CreateCommandBuffer()
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = device.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        var result = vk.AllocateCommandBuffers(device.VkDevice, in allocInfo, out commandBuffer);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to allocate command buffer: {0}", result);

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        result = vk.CreateFence(device.VkDevice, in fenceInfo, null, out fence);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to create fence: {0}", result);
    }

    void CreateStagingBuffer()
    {
        stagingSize = Width * Height * 4; // BGRA8

        BufferCreateInfo bufInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = stagingSize,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };

        var result = vk.CreateBuffer(device.VkDevice, in bufInfo, null, out stagingBuffer);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to create staging buffer: {0}", result);

        vk.GetBufferMemoryRequirements(device.VkDevice, stagingBuffer, out var memReq);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = device.FindMemoryType(memReq.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        result = vk.AllocateMemory(device.VkDevice, in allocInfo, null, out stagingMemory);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to allocate staging memory: {0}", result);

        _ = vk.BindBufferMemory(device.VkDevice, stagingBuffer, stagingMemory, 0);
    }

    void DestroyResources()
    {
        if (renderPass.Handle != 0)
        {
            vk.DestroyFramebuffer(device.VkDevice, framebuffer, null);
            framebuffer = default;

            vk.DestroyImageView(device.VkDevice, msaaColorView, null);
            vk.DestroyImage(device.VkDevice, msaaColorImage, null);
            vk.FreeMemory(device.VkDevice, msaaColorMemory, null);

            vk.DestroyImageView(device.VkDevice, resolveColorView, null);
            vk.DestroyImage(device.VkDevice, resolveColorImage, null);
            vk.FreeMemory(device.VkDevice, resolveColorMemory, null);

            vk.DestroyImageView(device.VkDevice, depthView, null);
            vk.DestroyImage(device.VkDevice, depthImage, null);
            vk.FreeMemory(device.VkDevice, depthMemory, null);

            vk.DestroyRenderPass(device.VkDevice, renderPass, null);
            renderPass = default;

            vk.FreeCommandBuffers(device.VkDevice, device.CommandPool, 1, in commandBuffer);
            vk.DestroyFence(device.VkDevice, fence, null);

            vk.DestroyBuffer(device.VkDevice, stagingBuffer, null);
            vk.FreeMemory(device.VkDevice, stagingMemory, null);
        }
    }
}
