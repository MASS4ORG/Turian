namespace Turian.Engine.Core;

/// <summary>
/// GPU copies of <see cref="InstancedModelComponent"/> instances: one device-local buffer per component, refreshed
/// through a per-frame staging ring with only the changed range, and culled as a whole by its aggregate bounds.
/// </summary>
sealed unsafe class InstanceBatches(Vulkan vulkan, DescriptorSetLayout layout, RetiredResources retired) : IDisposable
{
    readonly Dictionary<InstancedModelComponent, InstanceBatch> batches = [];
    readonly List<InstancedModelComponent> unseen = [];
    readonly Buffer?[] staging = new Buffer?[SwapChain.MaxFramesInFlight];

    /// <summary>The batches that passed culling in the latest <see cref="Prepare"/>, in scene order.</summary>
    public List<InstanceBatch> Visible { get; } = [];

    /// <summary>Selects the batches to draw this frame and releases those no longer in the scene.</summary>
    /// <param name="components">The active instanced components gathered this frame.</param>
    /// <param name="frustum">The camera frustum planes, or empty to skip culling.</param>
    /// <param name="stats">Receives the submeshes submitted and culled, one per instance and submesh.</param>
    public void Prepare(List<InstancedModelComponent> components, ReadOnlySpan<Plane> frustum,
        ref RenderCullingStats stats)
    {
        Visible.Clear();
        foreach (var component in components)
        {
            if (component.ModelInstance is not VulkanModel model || component.Node is null || component.Count == 0)
            {
                continue;
            }

            if (!batches.TryGetValue(component, out var batch))
            {
                batches[component] = batch = new InstanceBatch(component);
            }

            batch.Model = model;
            batch.LastSeen = retired.Frame;
            var submeshes = model.SubMeshes.Count * component.Count;
            if (!frustum.IsEmpty && !GeometryUtility.TestPlanesAabb(frustum, component.WorldBounds))
            {
                stats = stats with { Culled = stats.Culled + submeshes };
                continue;
            }

            stats = stats with { Submitted = stats.Submitted + submeshes };
            Visible.Add(batch);
        }

        ReleaseUnseen();
    }

    /// <summary>
    /// Records the copies of every visible batch's changed instances, ordered after earlier frames' reads of the same
    /// buffers and before this frame's vertex shaders. Must run outside a render pass.
    /// </summary>
    /// <param name="commandBuffer">The frame's command buffer.</param>
    /// <param name="frameIndex">The frame slot, selecting the staging buffer.</param>
    public void Upload(CommandBuffer commandBuffer, int frameIndex)
    {
        var total = 0;
        var pending = false;
        foreach (var batch in Visible)
        {
            var count = PlanUpload(batch);
            total += count;
            pending |= batch.Pending;
        }

        if (!pending)
        {
            return;
        }

        var slot = frameIndex % staging.Length;
        if (total > 0)
        {
            EnsureStaging(slot, total);
        }

        var instances = total > 0 ? staging[slot]!.AsSpan<InstanceData>() : [];
        Barrier(commandBuffer, PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, 0,
            AccessFlags.TransferWriteBit);
        var offset = 0;
        foreach (var batch in Visible)
        {
            if (!batch.Pending)
            {
                continue;
            }

            var count = batch.UploadEnd - batch.UploadStart;
            if (count > 0)
            {
                Write(batch, instances.Slice(offset, count));
                Copy(commandBuffer, staging[slot]!, batch, offset, count);
            }

            offset += count;
            Commit(batch);
        }

        Barrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    int PlanUpload(InstanceBatch batch)
    {
        var component = batch.Component;
        var node = component.Node!;
        batch.Pending = false;
        var full = EnsureCapacity(batch) || batch.NodeTransform != node.GlobalTransform
                                         || batch.Layer != node.RenderLayer;
        int start, end;
        if (full)
        {
            start = 0;
            end = component.Count;
        }
        else if (!component.TryGetChanges(batch.SyncedVersion, out start, out end))
        {
            return 0;
        }

        batch.UploadStart = start;
        batch.UploadEnd = end;
        batch.Pending = true;
        return end - start;
    }

    /// <summary>Gives the batch a buffer that fits its component; returns true when a new, empty one was allocated.</summary>
    bool EnsureCapacity(InstanceBatch batch)
    {
        var count = batch.Component.Count;
        if (batch.Buffer is { } current && current.Capacity >= count)
        {
            return false;
        }

        retired.Retire(batch.Buffer);
        var capacity = InstanceBuffer.Grow(batch.Buffer?.Capacity ?? 0, count);
        batch.Buffer = new InstanceBuffer(vulkan, layout, capacity, hostVisible: false);
        return true;
    }

    static void Write(InstanceBatch batch, Span<InstanceData> destination)
    {
        var component = batch.Component;
        var node = component.Node!;
        var global = node.GlobalTransform;
        var nodeModel = global.Matrix4X4();
        var nodeNormal = global.NormalMatrix();
        for (var i = 0; i < destination.Length; i++)
        {
            var instance = component[batch.UploadStart + i];
            destination[i] = InstanceData.Create(instance.Transform.Matrix4X4() * nodeModel,
                instance.Transform.NormalMatrix() * nodeNormal, node.RenderLayer, instance.Color,
                instance.CustomData);
        }
    }

    void Copy(CommandBuffer commandBuffer, Buffer source, InstanceBatch batch, int offset, int count)
    {
        BufferCopy region = new()
        {
            SrcOffset = (ulong)offset * InstanceData.Size,
            DstOffset = (ulong)batch.UploadStart * InstanceData.Size,
            Size = (ulong)count * InstanceData.Size,
        };
        vulkan.Vk.CmdCopyBuffer(commandBuffer, source.VkBuffer, batch.Buffer!.Buffer.VkBuffer, 1, in region);
    }

    static void Commit(InstanceBatch batch)
    {
        var component = batch.Component;
        batch.SyncedVersion = component.Version;
        batch.Count = component.Count;
        batch.NodeTransform = component.Node!.GlobalTransform;
        batch.Layer = component.Node.RenderLayer;
        batch.Pending = false;
        component.AcknowledgeChanges();
    }

    void EnsureStaging(int slot, int count)
    {
        var current = staging[slot];
        var capacity = current is null ? 0 : (int)current.InstanceCount;
        if (capacity >= count)
        {
            return;
        }

        retired.Retire(current);
        var buffer = new Buffer(vulkan, InstanceData.Size, (uint)InstanceBuffer.Grow(capacity, count),
            BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        if (buffer.Map() != Result.Success)
        {
            buffer.Dispose();
            throw new VulkanException("Instance staging buffer mapping failed.");
        }

        staging[slot] = buffer;
    }

    void Barrier(CommandBuffer commandBuffer, PipelineStageFlags source, PipelineStageFlags destination,
        AccessFlags sourceAccess, AccessFlags destinationAccess)
    {
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
        };
        vulkan.Vk.CmdPipelineBarrier(commandBuffer, source, destination, 0, 1, in barrier, 0, null, 0, null);
    }

    void ReleaseUnseen()
    {
        unseen.Clear();
        foreach (var (component, batch) in batches)
        {
            if (retired.Frame - batch.LastSeen > SwapChain.MaxFramesInFlight)
            {
                unseen.Add(component);
            }
        }

        foreach (var component in unseen)
        {
            retired.Retire(batches[component].Buffer);
            batches.Remove(component);
        }
    }

    /// <summary>Releases every buffer; the caller has waited for the device to go idle.</summary>
    public void Dispose()
    {
        foreach (var batch in batches.Values)
        {
            batch.Buffer?.Dispose();
        }

        batches.Clear();
        Visible.Clear();
        foreach (var buffer in staging)
        {
            buffer?.Dispose();
        }

        Array.Clear(staging);
    }
}

/// <summary>One <see cref="InstancedModelComponent"/>'s GPU copy and the state it was last uploaded from.</summary>
/// <param name="component">The component the batch draws.</param>
sealed class InstanceBatch(InstancedModelComponent component)
{
    /// <summary>The component the batch draws.</summary>
    public InstancedModelComponent Component { get; } = component;

    /// <summary>The model resolved for the current frame.</summary>
    public VulkanModel Model { get; set; } = null!;

    /// <summary>The device-local instance buffer, or null before the first upload.</summary>
    public InstanceBuffer? Buffer { get; set; }

    /// <summary>The component version last uploaded, or -1 for none.</summary>
    public int SyncedVersion { get; set; } = -1;

    /// <summary>The number of instances last uploaded, which is what a draw may read.</summary>
    public int Count { get; set; }

    /// <summary>The node transform baked into the uploaded instances.</summary>
    public Transform NodeTransform { get; set; }

    /// <summary>The render layer baked into the uploaded instances.</summary>
    public int Layer { get; set; } = -1;

    /// <summary>The frame the component was last gathered in.</summary>
    public long LastSeen { get; set; }

    /// <summary>Whether this frame records an upload, of the range from <see cref="UploadStart"/>.</summary>
    public bool Pending { get; set; }

    /// <summary>The first instance this frame uploads.</summary>
    public int UploadStart { get; set; }

    /// <summary>One past the last instance this frame uploads.</summary>
    public int UploadEnd { get; set; }
}
