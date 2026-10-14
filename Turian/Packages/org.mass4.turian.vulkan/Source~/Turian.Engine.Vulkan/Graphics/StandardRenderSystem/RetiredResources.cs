namespace Turian.Engine.Core;

/// <summary>Defers disposing GPU resources until no frame in flight can still read them.</summary>
sealed class RetiredResources : IDisposable
{
    readonly List<(IDisposable Resource, long Frame)> pending = [];

    /// <summary>The number of frames prepared so far.</summary>
    public long Frame { get; private set; }

    /// <summary>Queues <paramref name="resource"/> for disposal once every frame slot has moved past this frame.</summary>
    /// <param name="resource">The resource, or null for nothing.</param>
    public void Retire(IDisposable? resource)
    {
        if (resource is not null) pending.Add((resource, Frame));
    }

    /// <summary>Advances one frame and disposes the resources no frame in flight can still use.</summary>
    public void NextFrame()
    {
        Frame++;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            if (Frame - pending[i].Frame <= SwapChain.MaxFramesInFlight) continue;
            pending[i].Resource.Dispose();
            pending.RemoveAt(i);
        }
    }

    /// <summary>Disposes everything still queued; the caller has waited for the device to go idle.</summary>
    public void Dispose()
    {
        foreach (var (resource, _) in pending) resource.Dispose();
        pending.Clear();
    }
}
