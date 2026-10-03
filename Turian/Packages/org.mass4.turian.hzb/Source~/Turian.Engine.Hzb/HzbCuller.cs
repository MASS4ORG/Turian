namespace Turian.Engine.Hzb;

sealed unsafe class HzbCuller(Vulkan vulkan, Silk.NET.Vulkan.DescriptorSetLayout globalLayout) : IOcclusionCuller
{
    DescriptorSetLayout? reductionSet;
    DescriptorSetLayout? cullSet;
    PipelineLayout reductionLayout, cullLayout;
    Pipeline basePipeline, reductionPipeline, cullPipeline;
    HzbTarget? target;
    readonly Dictionary<int, HzbFrame> frames = [];
    Format depthFormat;
    bool? supported;
    bool disposed;

    /// <inheritdoc />
    public Silk.NET.Vulkan.Buffer IndirectBuffer { get; private set; }
    /// <inheritdoc />
    public RenderCullingStats CompletedStats { get; private set; }
    /// <inheritdoc />
    public ulong AllocatedBytes => (target?.Bytes ?? 0) + frames.Values.Aggregate(0UL, (sum, frame) => sum + frame.Bytes);

    /// <inheritdoc />
    public bool Record(FrameInfo frame, ReadOnlySpan<OcclusionDraw> draws)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (draws.IsEmpty || frame.ViewportWidth == 0 || frame.ViewportHeight == 0) return false;
        supported ??= CheckSupport();
        if (!supported.Value) return false;
        EnsureTarget(frame.ViewportWidth, frame.ViewportHeight);
        var slot = FrameSlot(frame.FrameIndex, draws.Length);
        CompletedStats = slot.ReadCounts();
        slot.Upload(draws);
        target!.RecordDepth(frame, draws);
        target.RecordPyramid(frame.CommandBuffer, reductionLayout, basePipeline, reductionPipeline);
        RecordCulling(frame, draws.Length, slot);
        IndirectBuffer = slot.Commands.VkBuffer;
        slot.Submitted = true;
        return true;
    }

    bool CheckSupport()
    {
        if (!SupportsComputeQueue() || !SupportsPyramidFormat()) return false;
        foreach (var format in new[] { Format.D32Sfloat, Format.D16Unorm })
        {
            if (!SupportsDepthFormat(format)) continue;
            depthFormat = format;
            return true;
        }
        return false;
    }

    bool SupportsComputeQueue()
    {
        uint count = 0;
        vulkan.Vk.GetPhysicalDeviceQueueFamilyProperties(vulkan.Device.VkPhysicalDevice, ref count, null);
        var queues = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* properties = queues)
            vulkan.Vk.GetPhysicalDeviceQueueFamilyProperties(vulkan.Device.VkPhysicalDevice, ref count, properties);
        return (queues[vulkan.Device.GraphicsFamilyIndex].QueueFlags & QueueFlags.ComputeBit) != 0;
    }

    bool SupportsDepthFormat(Format format)
    {
        var result = vulkan.Vk.GetPhysicalDeviceImageFormatProperties(vulkan.Device.VkPhysicalDevice, format,
            ImageType.Type2D, ImageTiling.Optimal,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, 0, out var properties);
        return result == Result.Success && (properties.SampleCounts & vulkan.Device.MsaaSamples) != 0;
    }

    bool SupportsPyramidFormat() => vulkan.Device.SupportsFormat(Format.R32Sfloat,
        FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit, ImageTiling.Optimal);

    void InitializePipelines()
    {
        reductionSet = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.CombinedImageSampler, ShaderStageFlags.ComputeBit)
            .AddBinding(1, DescriptorType.StorageImage, ShaderStageFlags.ComputeBit).Build();
        cullSet = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit)
            .AddBinding(1, DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit)
            .AddBinding(2, DescriptorType.CombinedImageSampler, ShaderStageFlags.ComputeBit)
            .AddBinding(3, DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit).Build();
        reductionLayout = HzbPipelines.Layout(vulkan, reductionSet.GetDescriptorSetLayout(), ShaderStageFlags.ComputeBit, 16);
        cullLayout = HzbPipelines.Layout(vulkan, cullSet.GetDescriptorSetLayout(), ShaderStageFlags.ComputeBit, 80);
        var baseName = vulkan.Device.MsaaSamples == SampleCountFlags.Count1Bit ? "base-single.comp.spv" : "base.comp.spv";
        basePipeline = HzbPipelines.Compute(vulkan, baseName, reductionLayout);
        reductionPipeline = HzbPipelines.Compute(vulkan, "reduce.comp.spv", reductionLayout);
        cullPipeline = HzbPipelines.Compute(vulkan, "cull.comp.spv", cullLayout);
    }

    void EnsureTarget(uint width, uint height)
    {
        if (target?.Width == width && target.Height == height) return;
        if (reductionSet is null) InitializePipelines();
        if (target is not null)
        {
            HzbPipelines.Check(vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice));
            ReleaseTarget();
        }
        target = new HzbTarget(vulkan, width, height, depthFormat, globalLayout, reductionSet!);
    }

    HzbFrame FrameSlot(int index, int count)
    {
        if (frames.TryGetValue(index, out var slot))
        {
            if (slot.Capacity >= count) return slot;
            slot.Dispose();
        }
        slot = new HzbFrame(vulkan, Math.Max(64, count), cullSet!, target!.PyramidInfo);
        frames[index] = slot;
        return slot;
    }

    void RecordCulling(FrameInfo frame, int count, HzbFrame slot)
    {
        var command = frame.CommandBuffer;
        var set = slot.Set;
        vulkan.Vk.CmdBindPipeline(command, PipelineBindPoint.Compute, cullPipeline);
        vulkan.Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, cullLayout, 0, 1, in set, 0, null);
        CullPush push = new(frame.Camera.GetViewMatrix() * frame.Camera.GetProjectionMatrix(),
            frame.ViewportWidth, frame.ViewportHeight, (uint)count, 0.0001f);
        vulkan.Vk.CmdPushConstants(command, cullLayout, ShaderStageFlags.ComputeBit, 0, 80, ref push);
        vulkan.Vk.CmdDispatch(command, ((uint)count + 63) / 64, 1, 1);
        HzbPipelines.Barrier(vulkan, command, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.HostBit,
            AccessFlags.ShaderWriteBit, AccessFlags.IndirectCommandReadBit | AccessFlags.HostReadBit);
    }

    void ReleaseTarget()
    {
        foreach (var frame in frames.Values) frame.Dispose();
        frames.Clear();
        target?.Dispose();
        target = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseTarget();
        vulkan.Vk.DestroyPipeline(vulkan.Device.VkDevice, basePipeline, null);
        vulkan.Vk.DestroyPipeline(vulkan.Device.VkDevice, reductionPipeline, null);
        vulkan.Vk.DestroyPipeline(vulkan.Device.VkDevice, cullPipeline, null);
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, reductionLayout, null);
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, cullLayout, null);
        reductionSet?.Dispose();
        cullSet?.Dispose();
    }

    readonly record struct CullPush(Matrix4x4 ViewProjection, uint Width, uint Height, uint Count, float Bias);
}
