namespace Turian.Engine.Hzb;

sealed unsafe class HzbTarget : IDisposable
{
    readonly Vulkan vulkan;
    readonly HzbImage depth;
    readonly Framebuffer framebuffer;
    readonly RenderPass renderPass;
    readonly PipelineLayout depthLayout;
    readonly Pipeline depthPipeline;
    readonly DescriptorPool pool;
    readonly DescriptorSet[] reductions;
    readonly Sampler sampler;
    internal HzbImage Pyramid { get; }
    internal uint Width => depth.Width;
    internal uint Height => depth.Height;
    internal ulong Bytes => depth.Bytes + Pyramid.Bytes;
    internal DescriptorImageInfo PyramidInfo => new(sampler, Pyramid.FullView, ImageLayout.General);

    internal HzbTarget(Vulkan vulkan, uint width, uint height, Format depthFormat,
        Silk.NET.Vulkan.DescriptorSetLayout globalLayout, DescriptorSetLayout reductionSet)
    {
        this.vulkan = vulkan;
        depth = new HzbImage(vulkan, width, height, 1, depthFormat, vulkan.Device.MsaaSamples,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.DepthBit);
        var paddedWidth = BitOperations.RoundUpToPowerOf2(width);
        var paddedHeight = BitOperations.RoundUpToPowerOf2(height);
        var levels = (uint)BitOperations.Log2(Math.Max(paddedWidth, paddedHeight)) + 1;
        Pyramid = new HzbImage(vulkan, paddedWidth, paddedHeight, levels, Format.R32Sfloat, SampleCountFlags.Count1Bit,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);
        renderPass = CreateRenderPass(depthFormat);
        var depthView = depth.FullView;
        FramebufferCreateInfo framebufferInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &depthView,
            Width = width,
            Height = height,
            Layers = 1
        };
        HzbPipelines.Check(vulkan.Vk.CreateFramebuffer(vulkan.Device.VkDevice, in framebufferInfo, null, out framebuffer));
        depthLayout = HzbPipelines.Layout(vulkan, globalLayout, ShaderStageFlags.VertexBit, 64);
        depthPipeline = HzbPipelines.Depth(vulkan, renderPass, depthLayout);
        sampler = CreateSampler();
        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device).SetMaxSets(levels)
            .AddPoolSize(DescriptorType.CombinedImageSampler, levels).AddPoolSize(DescriptorType.StorageImage, levels)
            .Build();
        reductions = new DescriptorSet[levels];
        for (uint i = 0; i < levels; i++)
        {
            var source = i == 0
                ? new DescriptorImageInfo(sampler, depth.FullView, ImageLayout.DepthStencilReadOnlyOptimal)
                : new DescriptorImageInfo(sampler, Pyramid.Views[i - 1], ImageLayout.General);
            var destination = new DescriptorImageInfo(default, Pyramid.Views[i], ImageLayout.General);
            if (!new DescriptorSetWriter(vulkan.Vk, vulkan.Device, reductionSet)
                .WriteImage(0, source).WriteImage(1, destination)
                .Build(pool, reductionSet.GetDescriptorSetLayout(), ref reductions[i]))
                throw new VulkanException("HZB descriptor allocation failed.");
        }
    }

    RenderPass CreateRenderPass(Format format)
    {
        AttachmentDescription attachment = new()
        {
            Format = format,
            Samples = vulkan.Device.MsaaSamples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal
        };
        AttachmentReference reference = new(0, ImageLayout.DepthStencilAttachmentOptimal);
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &reference
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ComputeShaderBit,
            DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = PipelineStageFlags.ComputeShaderBit,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        RenderPassCreateInfo info = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies
        };
        HzbPipelines.Check(vulkan.Vk.CreateRenderPass(vulkan.Device.VkDevice, in info, null, out var pass));
        return pass;
    }

    Sampler CreateSampler()
    {
        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = Pyramid.Levels
        };
        HzbPipelines.Check(vulkan.Vk.CreateSampler(vulkan.Device.VkDevice, in info, null, out var result));
        return result;
    }

    internal void RecordDepth(FrameInfo frame, ReadOnlySpan<OcclusionDraw> draws)
    {
        var command = frame.CommandBuffer;
        ClearValue clear = new() { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        RenderPassBeginInfo begin = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(default, new Extent2D(Width, Height)),
            ClearValueCount = 1,
            PClearValues = &clear
        };
        vulkan.Vk.CmdBeginRenderPass(command, in begin, SubpassContents.Inline);
        vulkan.Vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, depthPipeline);
        var global = frame.GlobalDescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, depthLayout, 0, 1, in global, 0, null);
        Viewport viewport = new(0, 0, Width, Height, 0, 1);
        Rect2D scissor = new(default, new Extent2D(Width, Height));
        vulkan.Vk.CmdSetViewport(command, 0, 1, in viewport);
        vulkan.Vk.CmdSetScissor(command, 0, 1, in scissor);
        foreach (ref readonly var draw in draws)
        {
            draw.Model.Bind(command);
            var model = draw.ModelMatrix;
            vulkan.Vk.CmdPushConstants(command, depthLayout, ShaderStageFlags.VertexBit, 0, 64, ref model);
            draw.Model.DrawSubMesh(command, draw.SubMesh);
        }
        vulkan.Vk.CmdEndRenderPass(command);
    }

    internal void RecordPyramid(CommandBuffer command, PipelineLayout layout, Pipeline basePipeline, Pipeline reduction)
    {
        ImageMemoryBarrier image = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = Pyramid.Image,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderWriteBit,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Pyramid.Levels, 0, 1)
        };
        vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit,
            0, 0, null, 0, null, 1, in image);
        BasePush push = new(Width, Height, (uint)vulkan.Device.MsaaSamples, 0);
        vulkan.Vk.CmdPushConstants(command, layout, ShaderStageFlags.ComputeBit, 0, 16, ref push);
        for (uint i = 0; i < Pyramid.Levels; i++)
        {
            vulkan.Vk.CmdBindPipeline(command, PipelineBindPoint.Compute, i == 0 ? basePipeline : reduction);
            var set = reductions[i];
            vulkan.Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, layout, 0, 1, in set, 0, null);
            var width = Math.Max(1, Pyramid.Width >> (int)i);
            var height = Math.Max(1, Pyramid.Height >> (int)i);
            vulkan.Vk.CmdDispatch(command, (width + 7) / 8, (height + 7) / 8, 1);
            HzbPipelines.Barrier(vulkan, command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        pool.Dispose();
        vulkan.Vk.DestroySampler(vulkan.Device.VkDevice, sampler, null);
        vulkan.Vk.DestroyPipeline(vulkan.Device.VkDevice, depthPipeline, null);
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, depthLayout, null);
        vulkan.Vk.DestroyFramebuffer(vulkan.Device.VkDevice, framebuffer, null);
        vulkan.Vk.DestroyRenderPass(vulkan.Device.VkDevice, renderPass, null);
        depth.Dispose();
        Pyramid.Dispose();
    }

    readonly record struct BasePush(uint Width, uint Height, uint Samples, uint Padding);
}
