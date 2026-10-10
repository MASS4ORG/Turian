namespace Turian.Engine.UI;

/// <summary>Draws Skia content directly into a texture on the engine's Vulkan graphics queue.</summary>
public sealed unsafe class GpuSkiaVulkanBackend : IUiRenderBackend, ICanvasRenderer
{
    const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit
                                  | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
    readonly Vulkan vulkan;
    readonly GRVkGetProcedureAddressDelegate getProcedureAddress;
    GRVkExtensions? extensions;
    GRContext? context;
    CpuSkiaVulkanBackend? fallback;
    Image image;
    ImageView view;
    DeviceMemory memory;
    CommandBuffer beginCommands;
    CommandBuffer endCommands;
    Fence fence;
    ImageLayout layout;
    bool disposed;

    /// <summary>Creates a shared Skia context, falling back to CPU rendering if Vulkan interop is unavailable.</summary>
    public GpuSkiaVulkanBackend(Vulkan vulkan, int width, int height)
        : this(vulkan, width, height, GRContext.CreateVulkan)
    { }

    internal GpuSkiaVulkanBackend(Vulkan vulkan, int width, int height,
        Func<GRVkBackendContext, GRContext?> createContext)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.vulkan = vulkan;
        Size = (width, height);
        getProcedureAddress = GetProcedureAddress;
        CreateContext(createContext);
        if (context is null) fallback = new CpuSkiaVulkanBackend(vulkan, width, height);
    }

    /// <inheritdoc />
    public (int Width, int Height) Size { get; private set; }

    /// <inheritdoc />
    public Texture? Texture { get; private set; }

    /// <summary>Whether rendering uses a Vulkan Skia context rather than the CPU fallback.</summary>
    public bool IsGpu => context is not null;

    void ICanvasRenderer.Initialize(int width, int height) => Resize(width, height);

    void CreateContext(Func<GRVkBackendContext, GRContext?> createContext)
    {
        var device = vulkan.Device;
        try
        {
            extensions = GRVkExtensions.Create(getProcedureAddress, device.Instance.Handle,
                device.VkPhysicalDevice.Handle, [.. device.InstanceExtensions], [.. device.DeviceExtensions]);
            using var backend = new GRVkBackendContext
            {
                VkInstance = device.Instance.Handle,
                VkPhysicalDevice = device.VkPhysicalDevice.Handle,
                VkDevice = device.VkDevice.Handle,
                VkQueue = device.GraphicsQueue.Handle,
                GraphicsQueueIndex = device.GraphicsFamilyIndex,
                MaxAPIVersion = device.ApiVersion,
                Extensions = extensions,
                GetProcedureAddress = getProcedureAddress,
            };
            context = createContext(backend);
        }
        catch (Exception exception)
        {
            Log.Logger.LogWarning(exception, "Skia Vulkan context creation failed; using CPU UI rendering");
        }
        if (context is not null) return;
        extensions?.Dispose();
        extensions = null;
    }

    IntPtr GetProcedureAddress(string name, IntPtr instance, IntPtr device) => device != IntPtr.Zero
        ? (IntPtr)vulkan.Vk.GetDeviceProcAddr(new Silk.NET.Vulkan.Device(device), name).Handle
        : (IntPtr)vulkan.Vk.GetInstanceProcAddr(new Instance(instance), name).Handle;

    /// <summary>Releases the old target after outstanding graphics work finishes and changes the target size.</summary>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (Size == (width, height)) return;
        ReleaseTarget();
        fallback?.Resize(width, height);
        Texture = null;
        Size = (width, height);
    }

    /// <summary>Draws one transparent UI frame and submits its transition for direct shader sampling.</summary>
    public void Render(Action<SKCanvas> draw)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(draw);
        if (fallback is not null)
        {
            fallback.Render(draw);
            Texture = fallback.Texture;
            return;
        }
        if (image.Handle == 0) CreateTarget();
        var vk = vulkan.Vk;
        var device = vulkan.Device.VkDevice;
        Check(vk.WaitForFences(device, 1, in fence, true, ulong.MaxValue));
        SubmitBarrier(beginCommands, layout, ImageLayout.ColorAttachmentOptimal, default);
        var info = new GRVkImageInfo
        {
            Image = image.Handle,
            ImageTiling = (uint)ImageTiling.Optimal,
            ImageLayout = (uint)ImageLayout.ColorAttachmentOptimal,
            Format = (uint)Format.R8G8B8A8Unorm,
            ImageUsageFlags = (uint)usage,
            SampleCount = 1,
            LevelCount = 1,
            CurrentQueueFamily = vulkan.Device.GraphicsFamilyIndex,
            SharingMode = (uint)SharingMode.Exclusive,
        };
        try
        {
            using var target = new GRBackendRenderTarget(Size.Width, Size.Height, info);
            using var surface = SKSurface.Create(context!, target, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888)
                                ?? throw new VulkanException("Skia could not wrap the UI render target");
            surface.Canvas.Clear(SKColors.Transparent);
            try { draw(surface.Canvas); }
            finally { context!.Flush(true, false); }
        }
        finally
        {
            Check(vk.ResetFences(device, 1, in fence));
            SubmitBarrier(endCommands, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, fence);
            layout = ImageLayout.ShaderReadOnlyOptimal;
        }
    }

    void CreateTarget()
    {
        var vk = vulkan.Vk;
        var device = vulkan.Device;
        try
        {
            device.CreateImage2D((uint)Size.Width, (uint)Size.Height, 1, Format.R8G8B8A8Unorm,
                ImageTiling.Optimal, usage, MemoryPropertyFlags.DeviceLocalBit, out image, out memory);
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = Format.R8G8B8A8Unorm,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            Check(vk.CreateImageView(device.VkDevice, in viewInfo, null, out view));
            Texture = new Texture(vulkan, (uint)Size.Width, (uint)Size.Height, viewInfo.Format, image, view,
                premultipliedSrgb: true);
            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = device.CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device.VkDevice, in allocate, out beginCommands));
            Check(vk.AllocateCommandBuffers(device.VkDevice, in allocate, out endCommands));
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            Check(vk.CreateFence(device.VkDevice, in fenceInfo, null, out fence));
            layout = ImageLayout.Undefined;
        }
        catch
        {
            ReleaseTarget();
            throw;
        }
    }

    void SubmitBarrier(CommandBuffer commands, ImageLayout oldLayout, ImageLayout newLayout, Fence signal)
    {
        var vk = vulkan.Vk;
        Check(vk.ResetCommandBuffer(commands, 0));
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(vk.BeginCommandBuffer(commands, in begin));
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = oldLayout == ImageLayout.Undefined ? 0 : AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = newLayout == ImageLayout.ShaderReadOnlyOptimal ? AccessFlags.ShaderReadBit
                : AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
        };
        vk.CmdPipelineBarrier(commands, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, in barrier);
        Check(vk.EndCommandBuffer(commands));
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commands,
        };
        Check(vk.QueueSubmit(vulkan.Device.GraphicsQueue, 1, in submit, signal));
    }

    static void Check(Result result)
    {
        if (result != Result.Success) throw new VulkanException($"Vulkan UI rendering failed: {result}");
    }

    void ReleaseTarget()
    {
        if (image.Handle == 0) return;
        var vk = vulkan.Vk;
        var device = vulkan.Device.VkDevice;
        Check(vk.DeviceWaitIdle(device));
        Texture?.Dispose();
        Texture = null;
        if (fence.Handle != 0) vk.DestroyFence(device, fence, null);
        if (beginCommands.Handle != 0) vk.FreeCommandBuffers(device, vulkan.Device.CommandPool, 1, in beginCommands);
        if (endCommands.Handle != 0) vk.FreeCommandBuffers(device, vulkan.Device.CommandPool, 1, in endCommands);
        if (view.Handle != 0) vk.DestroyImageView(device, view, null);
        vk.DestroyImage(device, image, null);
        if (memory.Handle != 0) vk.FreeMemory(device, memory, null);
        image = default;
        memory = default;
        view = default;
        fence = default;
        beginCommands = default;
        endCommands = default;
    }

    /// <summary>Releases GPU targets, the Skia context and any CPU fallback resources.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseTarget();
        fallback?.Dispose();
        Texture = null;
        context?.Dispose();
        context = null;
        extensions?.Dispose();
    }
}
