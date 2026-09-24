namespace Turian.Engine.Core;

/// <summary>
/// Represents a Vulkan device and its associated resources for graphics and rendering operations.
/// This class encapsulates functionality for creating and managing a Vulkan device, interfacing with surfaces,
/// and providing access to Vulkan instance-related functionality.
/// </summary>
/// <remarks>
/// The Vulkan device is a core component for graphics rendering and computation in Vulkan-based applications.
/// It handles resource management, command buffer creation, and other critical aspects of rendering.
/// The class also provides access to the Vulkan instance, which manages global state for Vulkan.
/// </remarks>
public unsafe partial class Device : IDisposable
{
    /// <summary>
    /// Gets the Vulkan instance associated with this device.
    /// </summary>
    public Instance Instance => instance;

    /// <summary>
    /// Gets the Vulkan device.
    /// </summary>
    public Silk.NET.Vulkan.Device VkDevice => device;

    /// <summary>
    /// Gets the surface associated with this device.
    /// </summary>
    public SurfaceKHR Surface => surface;

    /// <summary>
    /// Gets the physical device associated with this device.
    /// </summary>
    public PhysicalDevice VkPhysicalDevice => physicalDevice;

    /// <summary>
    /// Gets the name of the Vulkan device.
    /// </summary>
    public string DeviceName => deviceName;

    /// <summary>
    /// Gets the largest anisotropy value the device accepts on a sampler. Anisotropic filtering is
    /// always enabled — <c>IsDeviceSuitable</c> rejects devices that do not support it.
    /// </summary>
    public float MaxSamplerAnisotropy => maxSamplerAnisotropy;

    /// <summary>
    /// Gets the total size of the device-local memory heaps, in bytes — the VRAM a discrete GPU
    /// reports, or the share of system memory an integrated one exposes.
    /// </summary>
    /// <remarks>
    /// This is the heap size, not the amount currently free: reporting live usage needs
    /// <c>VK_EXT_memory_budget</c>, which the engine does not enable. Treat it as the ceiling a
    /// scene has to fit inside alongside the driver's own allocations.
    /// </remarks>
    public ulong DeviceLocalMemoryBytes => deviceLocalMemoryBytes;

    /// <summary>
    /// Gets the MSAA (Multi-Sample Anti-Aliasing) sample count.
    /// </summary>
    public SampleCountFlags MsaaSamples => msaaSamples;

    /// <summary>
    /// Gets the index of the graphics family queue.
    /// </summary>
    public uint GraphicsFamilyIndex => graphicsFamilyIndex;

    /// <summary>
    /// Gets the graphics queue associated with this device.
    /// </summary>
    public Queue GraphicsQueue => graphicsQueue;

    /// <summary>
    /// Gets the present queue associated with this device.
    /// </summary>
    public Queue PresentQueue => presentQueue;

    /// <summary>
    /// Gets the command pool associated with this device.
    /// </summary>
    public CommandPool CommandPool => commandPool;

    /// <summary>
    /// Gets the queue families found for this device.
    /// </summary>
    public QueueFamilyIndices FindQueueFamilies => FindQueueFamiliesInternal(physicalDevice);

    /// <summary>
    /// Gets the swap chain support details for this device.
    /// </summary>
    public SwapChainSupportDetails QuerySwapChainSupport =>
        QuerySwapChainSupportInternal(physicalDevice);

    readonly Vk vk;
    readonly IView? window;
    bool IsHeadless => window is null;

    ExtDebugUtils debugUtils = null!;
    readonly bool enableValidationLayers = false;
    readonly string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];

    readonly string[] deviceExtensions =
    [
        KhrSwapchain.ExtensionName,
        KhrSynchronization2.ExtensionName,
        "VK_EXT_mesh_shader",
        //"VK_KHR_spirv_1_4",
        //"VK_KHR_shader_float_controls",
    ];

    Instance instance;
    KhrSurface khrSurface = null!;
    SurfaceKHR surface;
    PhysicalDevice physicalDevice;
    string deviceName = "unknown";
    float maxSamplerAnisotropy = 1f;
    ulong deviceLocalMemoryBytes;
    SampleCountFlags msaaSamples = SampleCountFlags.Count1Bit;
    Silk.NET.Vulkan.Device device;
    uint graphicsFamilyIndex;
    Queue graphicsQueue;
    Queue presentQueue;
    CommandPool commandPool;

    /// <summary>
    /// Initializes a new instance of the <see cref="Device"/> class.
    /// </summary>
    /// <param name="vk">The Vulkan instance.</param>
    /// <param name="window">The window view. Pass <c>null</c> for headless (editor) mode.</param>
    public Device(Vk vk, IView? window = null)
    {
        this.vk = vk;
        this.window = window;
        CreateInstance();
        SetupDebugMessenger();
        if (!IsHeadless) CreateSurface();
        CreateLogicalDevice();
        CreateCommandPool();

        Log.Logger.Lap("startup", "Vulkan logical device and command pool created");
    }

    /// <summary>
    /// Gets the properties of the physical device.
    /// </summary>
    /// <returns>The properties of the physical device.</returns>
    public PhysicalDeviceProperties GetProperties()
    {
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        return properties;
    }

    /// <summary>
    /// Copies data from one buffer to another.
    /// </summary>
    /// <param name="srcBuffer">The source buffer.</param>
    /// <param name="dstBuffer">The destination buffer.</param>
    /// <param name="size">The size of the data to copy.</param>
    public void CopyBuffer(
        Silk.NET.Vulkan.Buffer srcBuffer,
        Silk.NET.Vulkan.Buffer dstBuffer,
        ulong size
    )
    {
        var commandBuffer = BeginSingleTimeCommands();

        BufferCopy copyRegion = new() { Size = size, };

        vk.CmdCopyBuffer(commandBuffer, srcBuffer, dstBuffer, 1, in copyRegion);

        EndSingleTimeCommands(commandBuffer);
    }

    /// <summary>
    /// Creates a buffer with the specified size, usage, and memory properties.
    /// </summary>
    /// <param name="size">The size of the buffer.</param>
    /// <param name="usage">The buffer usage flags.</param>
    /// <param name="properties">The memory properties.</param>
    /// <param name="buffer">The created buffer.</param>
    /// <param name="bufferMemory">The memory for the buffer.</param>
    public void CreateBuffer(
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        ref Silk.NET.Vulkan.Buffer buffer,
        ref DeviceMemory bufferMemory
    )
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Silk.NET.Vulkan.Buffer* bufferPtr = &buffer)
        {
            var resultVulkan = vk.CreateBuffer(device, in bufferInfo, null, bufferPtr);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create vertex buffer: {0}", resultVulkan);
            }
        }

        MemoryRequirements memRequirements;
        vk.GetBufferMemoryRequirements(device, buffer, out memRequirements);

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties),
        };

        fixed (DeviceMemory* bufferMemoryPtr = &bufferMemory)
        {
            var resultVulkan = vk.AllocateMemory(device, in allocateInfo, null, bufferMemoryPtr);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to allocate vertex buffer memory: {0}", resultVulkan);
            }
        }

        _ = vk.BindBufferMemory(device, buffer, bufferMemory, 0);
    }

    /// <summary>
    /// Finds the depth format supported by the device.
    /// </summary>
    /// <returns>The supported depth format.</returns>
    public Format FindDepthFormat() =>
        FindSupportedFormat(
            new[] { Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint },
            ImageTiling.Optimal,
            FormatFeatureFlags.DepthStencilAttachmentBit
        );

    /// <summary>
    /// Creates a 2D Vulkan image with backing device memory bound. Used by the texture
    /// pipeline; samplers and image views are created separately by <see cref="Texture"/>.
    /// </summary>
    public void CreateImage2D(
        uint width,
        uint height,
        uint mipLevels,
        Format format,
        ImageTiling tiling,
        ImageUsageFlags usage,
        MemoryPropertyFlags properties,
        out Image image,
        out DeviceMemory memory)
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit,
        };

        if (vk.CreateImage(device, in info, null, out image) != Result.Success)
            throw new VulkanException("Vulkan: failed to create image");

        vk.GetImageMemoryRequirements(device, image, out var req);
        MemoryAllocateInfo alloc = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, properties),
        };

        if (vk.AllocateMemory(device, in alloc, null, out memory) != Result.Success)
            throw new VulkanException("Vulkan: failed to allocate image memory");

        _ = vk.BindImageMemory(device, image, memory, 0);
    }

    /// <summary>
    /// Issues a layout transition pipeline barrier for the entire image (all mip levels).
    /// Supports Undefined→TransferDstOptimal and TransferDstOptimal→ShaderReadOnlyOptimal —
    /// the two transitions a staged texture upload needs.
    /// </summary>
    public void TransitionImageLayout(Image image, ImageLayout oldLayout, ImageLayout newLayout, uint mipLevels)
    {
        var cmd = BeginSingleTimeCommands();
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, 1),
        };

        PipelineStageFlags srcStage, dstStage;
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new VulkanException($"Unsupported image layout transition: {oldLayout} -> {newLayout}");
        }

        vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
        EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Copies a tightly-packed buffer into mip 0 of a 2D image. The image must already be
    /// in <see cref="ImageLayout.TransferDstOptimal"/>.
    /// </summary>
    public void CopyBufferToImage(Silk.NET.Vulkan.Buffer src, Image dst, uint width, uint height)
    {
        var cmd = BeginSingleTimeCommands();
        BufferImageCopy region = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        vk.CmdCopyBufferToImage(cmd, src, dst, ImageLayout.TransferDstOptimal, 1, in region);
        EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Copies a staging buffer into a 2D image using one region per mip level. The image must
    /// already be in <see cref="ImageLayout.TransferDstOptimal"/>. Compressed formats supply their
    /// mip chain this way; there is no row pitch, only whole blocks.
    /// </summary>
    /// <param name="src">Staging buffer holding every level back to back.</param>
    /// <param name="dst">Destination image.</param>
    /// <param name="regions">One copy region per mip level.</param>
    public void CopyBufferToImage(Silk.NET.Vulkan.Buffer src, Image dst, ReadOnlySpan<BufferImageCopy> regions)
    {
        if (regions.IsEmpty) return;

        var cmd = BeginSingleTimeCommands();
        fixed (BufferImageCopy* pRegions = regions)
        {
            vk.CmdCopyBufferToImage(cmd, src, dst, ImageLayout.TransferDstOptimal, (uint)regions.Length, pRegions);
        }

        EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Fills mip levels 1..<paramref name="mipLevels"/>-1 by successively halving the level above
    /// with <c>vkCmdBlitImage</c>, and leaves the whole image in
    /// <see cref="ImageLayout.ShaderReadOnlyOptimal"/>. The image must be in
    /// <see cref="ImageLayout.TransferDstOptimal"/> with level 0 already uploaded, and its format
    /// must support linear blitting — check with <see cref="SupportsFormat"/> first, as
    /// block-compressed formats do not.
    /// </summary>
    /// <param name="image">The image to fill.</param>
    /// <param name="width">Width of mip level 0.</param>
    /// <param name="height">Height of mip level 0.</param>
    /// <param name="mipLevels">Total level count, including level 0.</param>
    public void GenerateMipmaps(Image image, uint width, uint height, uint mipLevels)
    {
        var cmd = BeginSingleTimeCommands();

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        var mipWidth = (int)width;
        var mipHeight = (int)height;

        for (var level = 1u; level < mipLevels; level++)
        {
            // Wait for level-1 to finish being written, then read it as the blit source.
            barrier.SubresourceRange.BaseMipLevel = level - 1;
            barrier.OldLayout = ImageLayout.TransferDstOptimal;
            barrier.NewLayout = ImageLayout.TransferSrcOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            vk.CmdPipelineBarrier(
                cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, in barrier);

            var nextWidth = Math.Max(1, mipWidth / 2);
            var nextHeight = Math.Max(1, mipHeight / 2);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1),
            };
            blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);

            vk.CmdBlitImage(
                cmd,
                image, ImageLayout.TransferSrcOptimal,
                image, ImageLayout.TransferDstOptimal,
                1, in blit, Filter.Linear);

            // The source level is finished with — hand it to the shader.
            barrier.OldLayout = ImageLayout.TransferSrcOptimal;
            barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            vk.CmdPipelineBarrier(
                cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, in barrier);

            mipWidth = nextWidth;
            mipHeight = nextHeight;
        }

        // The last level was never a blit source, so it is still TransferDstOptimal.
        barrier.SubresourceRange.BaseMipLevel = mipLevels - 1;
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        vk.CmdPipelineBarrier(
            cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in barrier);

        EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Reports whether the physical device offers <paramref name="features"/> for
    /// <paramref name="format"/> at the given tiling. Used to check block-compressed sampling
    /// support before creating a BC texture, and linear-blit support before generating mips.
    /// </summary>
    /// <param name="format">The format to query.</param>
    /// <param name="features">The required format features.</param>
    /// <param name="tiling">The image tiling the format will be used with.</param>
    public bool SupportsFormat(Format format, FormatFeatureFlags features, ImageTiling tiling)
    {
        vk.GetPhysicalDeviceFormatProperties(physicalDevice, format, out var props);
        var available = tiling == ImageTiling.Linear ? props.LinearTilingFeatures : props.OptimalTilingFeatures;
        return (available & features) == features;
    }

    /// <summary>
    /// Finds a suitable memory type for the specified type filter and memory properties.
    /// </summary>
    /// <param name="typeFilter">The type filter.</param>
    /// <param name="properties">The memory properties.</param>
    /// <returns>The memory type index.</returns>
    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memProperties);

        for (var i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if (
                (typeFilter & (1 << i)) != 0
                && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties
            )
            {
                return (uint)i;
            }
        }

        throw new VulkanException("failed to find suitable memory type!");
    }

}
