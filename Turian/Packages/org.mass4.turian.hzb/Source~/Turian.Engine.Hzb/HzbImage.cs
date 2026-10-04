namespace Turian.Engine.Hzb;

sealed unsafe class HzbImage : IDisposable
{
    readonly Vulkan vulkan;
    readonly DeviceMemory memory;
    internal Image Image { get; }
    internal ImageView[] Views { get; }
    internal ImageView FullView { get; }
    internal uint Width { get; }
    internal uint Height { get; }
    internal uint Levels { get; }
    internal ulong Bytes { get; }

    internal HzbImage(Vulkan vulkan, uint width, uint height, uint levels, Format format,
        SampleCountFlags samples, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        this.vulkan = vulkan;
        Width = width;
        Height = height;
        Levels = levels;
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = levels,
            ArrayLayers = 1,
            Format = format,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        HzbPipelines.Check(vulkan.Vk.CreateImage(vulkan.Device.VkDevice, in info, null, out var image));
        Image = image;
        vulkan.Vk.GetImageMemoryRequirements(vulkan.Device.VkDevice, image, out var requirements);
        Bytes = requirements.Size;
        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = vulkan.Device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        HzbPipelines.Check(vulkan.Vk.AllocateMemory(vulkan.Device.VkDevice, in allocation, null, out memory));
        HzbPipelines.Check(vulkan.Vk.BindImageMemory(vulkan.Device.VkDevice, image, memory, 0));
        Views = new ImageView[levels];
        for (uint i = 0; i < levels; i++) Views[i] = View(format, aspect, i, 1);
        FullView = View(format, aspect, 0, levels);
    }

    ImageView View(Format format, ImageAspectFlags aspect, uint start, uint count)
    {
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, start, count, 0, 1)
        };
        HzbPipelines.Check(vulkan.Vk.CreateImageView(vulkan.Device.VkDevice, in info, null, out var view));
        return view;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var view in Views) vulkan.Vk.DestroyImageView(vulkan.Device.VkDevice, view, null);
        vulkan.Vk.DestroyImageView(vulkan.Device.VkDevice, FullView, null);
        vulkan.Vk.DestroyImage(vulkan.Device.VkDevice, Image, null);
        vulkan.Vk.FreeMemory(vulkan.Device.VkDevice, memory, null);
    }
}
