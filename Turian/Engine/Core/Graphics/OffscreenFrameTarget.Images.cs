namespace Turian.Engine.Core;

public unsafe partial class OffscreenFrameTarget
{
    void CreateImage(uint w, uint h, Format format, SampleCountFlags samples,
        ImageUsageFlags usage, MemoryPropertyFlags memProps,
        out Image image, out DeviceMemory memory)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(w, h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = samples,
            SharingMode = SharingMode.Exclusive
        };

        var result = vk.CreateImage(device.VkDevice, in imageInfo, null, out image);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to create image: {0}", result);

        vk.GetImageMemoryRequirements(device.VkDevice, image, out var memReq);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = device.FindMemoryType(memReq.MemoryTypeBits, memProps)
        };

        result = vk.AllocateMemory(device.VkDevice, in allocInfo, null, out memory);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to allocate image memory: {0}", result);

        _ = vk.BindImageMemory(device.VkDevice, image, memory, 0);
    }

    ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspectFlags)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspectFlags,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        var result = vk.CreateImageView(device.VkDevice, in viewInfo, null, out var view);
        if (result != Result.Success)
            throw new VulkanException("OffscreenFrameTarget: failed to create image view: {0}", result);

        return view;
    }
}
