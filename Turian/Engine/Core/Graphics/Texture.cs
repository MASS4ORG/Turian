namespace Turian.Engine.Core;

/// <summary>
/// A 2D Vulkan texture: device-local image + view + sampler. Construction uploads every supplied
/// mip level through one staging buffer, transitions the image to
/// <see cref="ImageLayout.ShaderReadOnlyOptimal"/> and creates a sampler covering the whole chain.
/// Block-compressed levels are uploaded as blocks, never decompressed.
/// </summary>
public sealed unsafe class Texture : IDisposable
{
    readonly Vulkan vulkan;
    Image image;
    DeviceMemory memory;
    ImageView view;
    Sampler sampler;
    bool disposed;

    /// <summary>Image width in pixels.</summary>
    public uint Width { get; }

    /// <summary>Image height in pixels.</summary>
    public uint Height { get; }

    /// <summary>Number of mip levels the image holds, at least one.</summary>
    public uint MipLevels { get; }

    /// <summary>The Vulkan format of the image.</summary>
    public Format Format { get; }

    /// <summary>Bytes of device memory the image occupies across every mip level.</summary>
    public ulong SizeBytes { get; }

    /// <summary>Combined image-sampler descriptor info for binding into a descriptor set.</summary>
    public DescriptorImageInfo DescriptorInfo => new()
    {
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        ImageView = view,
        Sampler = sampler,
    };

    /// <summary>
    /// Creates a texture from pre-supplied mip levels, tightly packed and ordered largest first.
    /// This is the path block-compressed data takes: the blocks go to the GPU untouched.
    /// </summary>
    /// <param name="vulkan">The Vulkan context the texture is created on.</param>
    /// <param name="format">Vulkan format of the supplied data.</param>
    /// <param name="width">Width of mip level 0.</param>
    /// <param name="height">Height of mip level 0.</param>
    /// <param name="levels">One entry per mip level, largest first. Must hold at least one.</param>
    /// <param name="addressMode">Sampler addressing for all three axes.</param>
    /// <exception cref="VulkanException">The device cannot sample <paramref name="format"/>.</exception>
    public Texture(
        Vulkan vulkan,
        Format format,
        uint width,
        uint height,
        IReadOnlyList<ReadOnlyMemory<byte>> levels,
        SamplerAddressMode addressMode = SamplerAddressMode.Repeat)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
            throw new ArgumentException("A texture needs at least one mip level", nameof(levels));

        if (!vulkan.Device.SupportsFormat(format, FormatFeatureFlags.SampledImageBit, ImageTiling.Optimal))
            throw new VulkanException($"Vulkan: device cannot sample texture format {format}");

        this.vulkan = vulkan;
        Width = width;
        Height = height;
        MipLevels = (uint)levels.Count;
        Format = format;
        SizeBytes = TotalLevelBytes(format, width, height, levels.Count);

        CreateImage(ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit);
        UploadLevels(levels);
        vulkan.Device.TransitionImageLayout(
            image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, MipLevels);

        CreateView();
        CreateSampler(addressMode);
    }

    /// <summary>
    /// Creates a 2D texture from tightly-packed RGBA8 pixels. <paramref name="isSrgb"/>
    /// selects between <c>R8G8B8A8Srgb</c> (color textures: base color, emissive) and
    /// <c>R8G8B8A8Unorm</c> (linear data textures: normal, metallic-roughness, AO).
    /// When <paramref name="generateMips"/> is set and the device can blit the format linearly,
    /// the full chain down to 1×1 is generated on the GPU.
    /// </summary>
    /// <param name="vulkan">The Vulkan context the texture is created on.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="rgba">Tightly-packed RGBA8 pixels for mip level 0.</param>
    /// <param name="isSrgb">Whether the data is sampled in sRGB space.</param>
    /// <param name="generateMips">Whether to generate the remaining mip levels by blitting.</param>
    /// <param name="addressMode">Sampler addressing for all three axes.</param>
    public Texture(
        Vulkan vulkan,
        uint width,
        uint height,
        ReadOnlySpan<byte> rgba,
        bool isSrgb,
        bool generateMips = false,
        SamplerAddressMode addressMode = SamplerAddressMode.Repeat)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} RGBA bytes, got {rgba.Length}", nameof(rgba));

        this.vulkan = vulkan;
        Width = width;
        Height = height;
        Format = isSrgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;

        // Blitting the chain needs the format to filter linearly as both source and destination.
        var blitFeatures = FormatFeatureFlags.BlitSrcBit
                           | FormatFeatureFlags.BlitDstBit
                           | FormatFeatureFlags.SampledImageFilterLinearBit;
        var canBlit = vulkan.Device.SupportsFormat(Format, blitFeatures, ImageTiling.Optimal);
        if (generateMips && !canBlit)
        {
            Log.Logger.LogWarning("Device cannot blit {Format} linearly; uploading a single mip level", Format);
        }

        var wantMips = generateMips && canBlit;
        MipLevels = wantMips ? TextureFormats.FullMipChainLength(width, height) : 1;
        SizeBytes = TotalLevelBytes(Format, width, height, (int)MipLevels);

        var usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit;
        if (wantMips) usage |= ImageUsageFlags.TransferSrcBit;
        CreateImage(usage);

        UploadLevels([rgba.ToArray()]);

        if (wantMips)
        {
            vulkan.Device.GenerateMipmaps(image, width, height, MipLevels);
        }
        else
        {
            vulkan.Device.TransitionImageLayout(
                image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, MipLevels);
        }

        CreateView();
        CreateSampler(addressMode);
    }

    /// <summary>
    /// Replaces mip level 0 with fresh, tightly-packed RGBA8 pixels. Valid only for a
    /// single-level texture, which is the shape render-to-texture callers (the UI compositor,
    /// video surfaces) use. The image is transitioned out of shader-read, the pixels are copied
    /// through a transient staging buffer, and it is transitioned back. Every step blocks on the
    /// graphics queue, so treat this as a synchronous upload and call it at most once per frame.
    /// </summary>
    /// <param name="rgba">Tightly-packed RGBA8 pixels for mip level 0; length must be Width*Height*4.</param>
    /// <exception cref="InvalidOperationException">The texture has more than one mip level.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> is not exactly Width*Height*4 bytes.</exception>
    public void Update(ReadOnlySpan<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (MipLevels != 1)
            throw new InvalidOperationException("Texture.Update requires a single-mip texture");
        if (rgba.Length != Width * Height * 4)
            throw new ArgumentException($"Expected {Width * Height * 4} RGBA bytes, got {rgba.Length}", nameof(rgba));

        // Undefined as the source layout discards the previous contents, which is exactly right when
        // the whole level is about to be overwritten, and is a transition the device path supports.
        vulkan.Device.TransitionImageLayout(image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 1);
        UploadLevels([rgba.ToArray()]);
        vulkan.Device.TransitionImageLayout(
            image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 1);
    }

    static ulong TotalLevelBytes(Format format, uint width, uint height, int levelCount)
    {
        ulong total = 0;
        for (var level = 0; level < levelCount; level++)
        {
            var (levelWidth, levelHeight) = TextureFormats.LevelExtent(width, height, level);
            total += TextureFormats.LevelSizeBytes(format, levelWidth, levelHeight);
        }

        return total;
    }

    void CreateImage(ImageUsageFlags usage)
    {
        vulkan.Device.CreateImage2D(
            Width, Height, MipLevels, Format,
            ImageTiling.Optimal,
            usage,
            MemoryPropertyFlags.DeviceLocalBit,
            out image, out memory);

        vulkan.Device.TransitionImageLayout(image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, MipLevels);
    }

    /// <summary>
    /// Packs every supplied level back to back into one staging buffer and issues a single copy
    /// with one region per level. Levels are tightly packed, so each offset already satisfies the
    /// element-size alignment the copy requires.
    /// </summary>
    void UploadLevels(IReadOnlyList<ReadOnlyMemory<byte>> levels)
    {
        var staged = new byte[levels.Sum(level => level.Length)];
        var regions = new BufferImageCopy[levels.Count];

        var offset = 0;
        for (var level = 0; level < levels.Count; level++)
        {
            levels[level].Span.CopyTo(staged.AsSpan(offset));
            var (levelWidth, levelHeight) = TextureFormats.LevelExtent(Width, Height, level);

            regions[level] = new BufferImageCopy
            {
                BufferOffset = (ulong)offset,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(levelWidth, levelHeight, 1),
            };

            offset += levels[level].Length;
        }

        using var staging = new Buffer(
            vulkan,
            instanceSize: (ulong)staged.Length,
            instanceCount: 1,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = staging.Map();
        staging.WriteBytesToBuffer(staged);

        vulkan.Device.CopyBufferToImage(staging.VkBuffer, image, regions);
    }

    void CreateView()
    {
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, MipLevels, 0, 1),
        };

        if (vulkan.Vk.CreateImageView(vulkan.Device.VkDevice, in info, null, out view) != Result.Success)
            throw new VulkanException("Vulkan: failed to create image view for texture");
    }

    void CreateSampler(SamplerAddressMode addressMode)
    {
        // MaxLod comes from the level count, never from the extent: DDS chains are often truncated
        // well above 1x1, and sampling past the last level reads undefined data.
        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = addressMode,
            AddressModeV = addressMode,
            AddressModeW = addressMode,
            AnisotropyEnable = Vk.True,
            MaxAnisotropy = vulkan.Device.MaxSamplerAnisotropy,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = Vk.False,
            CompareEnable = Vk.False,
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Linear,
            MinLod = 0f,
            MaxLod = MipLevels,
            MipLodBias = 0f,
        };

        if (vulkan.Vk.CreateSampler(vulkan.Device.VkDevice, in info, null, out sampler) != Result.Success)
            throw new VulkanException("Vulkan: failed to create sampler");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        var dev = vulkan.Device.VkDevice;
        if (sampler.Handle != 0) vulkan.Vk.DestroySampler(dev, sampler, null);
        if (view.Handle != 0) vulkan.Vk.DestroyImageView(dev, view, null);
        if (image.Handle != 0) vulkan.Vk.DestroyImage(dev, image, null);
        if (memory.Handle != 0) vulkan.Vk.FreeMemory(dev, memory, null);
    }
}
