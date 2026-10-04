namespace Turian.Engine.Hzb;

sealed unsafe class HzbFrame : IDisposable
{
    readonly DescriptorPool pool;
    readonly GpuCandidate[] candidates;
    internal Buffer Input { get; }
    internal Buffer Commands { get; }
    internal Buffer Counts { get; }
    internal DescriptorSet Set { get; }
    internal int Capacity => candidates.Length;
    internal bool Submitted { get; set; }
    internal ulong Bytes => Input.BufferSize + Commands.BufferSize + Counts.BufferSize;

    internal HzbFrame(Vulkan vulkan, int capacity, DescriptorSetLayout layout, DescriptorImageInfo pyramid)
    {
        candidates = new GpuCandidate[capacity];
        Input = new Buffer(vulkan, 64, (uint)capacity, BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        Commands = new Buffer(vulkan, 20, (uint)capacity,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        Counts = new Buffer(vulkan, 8, 1, BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        HzbPipelines.Check(Input.Map());
        HzbPipelines.Check(Counts.Map());
        pool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device).SetMaxSets(1)
            .AddPoolSize(DescriptorType.StorageBuffer, 3).AddPoolSize(DescriptorType.CombinedImageSampler, 1).Build();
        DescriptorSet set = default;
        if (!new DescriptorSetWriter(vulkan.Vk, vulkan.Device, layout)
            .WriteBuffer(0, Input.DescriptorInfo()).WriteBuffer(1, Commands.DescriptorInfo())
            .WriteImage(2, pyramid).WriteBuffer(3, Counts.DescriptorInfo())
            .Build(pool, layout.GetDescriptorSetLayout(), ref set))
            throw new VulkanException("HZB frame descriptor allocation failed.");
        Set = set;
    }

    internal RenderCullingStats ReadCounts()
    {
        if (!Submitted) return default;
        var counts = Counts.ReadFromBuffer<ulong>();
        return new RenderCullingStats((int)(counts & uint.MaxValue), (int)(counts >> 32));
    }

    internal void Upload(ReadOnlySpan<OcclusionDraw> draws)
    {
        for (var i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            var bounds = draw.WorldBounds;
            var submesh = draw.Model.SubMeshes[draw.SubMesh];
            candidates[i] = new GpuCandidate
            {
                Minimum = new Vector4(bounds.Min, bounds.IsEmpty ? 0 : 1),
                Maximum = new Vector4(bounds.Max, 0),
                Count = submesh.IndexCount,
                Instances = 1,
                Start = submesh.IndexStart,
                Offset = 0
            };
        }
        Input.WriteToBuffer(candidates);
        Counts.WriteToBuffer(0UL);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        pool.Dispose();
        Input.Dispose();
        Commands.Dispose();
        Counts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuCandidate
    {
        public Vector4 Minimum, Maximum;
        public uint Count, Instances, Start, Offset;
        public Vector4 Padding;
    }
}
