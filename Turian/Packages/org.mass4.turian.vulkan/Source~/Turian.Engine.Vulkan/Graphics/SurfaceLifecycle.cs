namespace Turian.Engine.Core;

/// <summary>The image and depth formats of a swap chain, which its render pass and pipelines are built for.</summary>
/// <param name="Image">The color image format.</param>
/// <param name="Depth">The depth attachment format.</param>
readonly record struct SwapChainFormats(Format Image, Format Depth);

/// <summary>The Vulkan objects behind a presentable surface, which <see cref="SurfaceLifecycle"/> destroys and rebuilds.</summary>
interface ISwapChainTarget
{
    /// <summary>Gets whether a swap chain currently exists.</summary>
    bool HasSwapChain { get; }

    /// <summary>Gets whether the surface reports a drawable area a swap chain can be built for.</summary>
    bool SurfaceHasArea { get; }

    /// <summary>Waits until the device has finished all submitted work.</summary>
    void WaitIdle();

    /// <summary>Creates the Vulkan surface for the host's current native surface.</summary>
    void CreateSurface();

    /// <summary>Destroys the Vulkan surface.</summary>
    void DestroySurface();

    /// <summary>Builds a swap chain of <paramref name="extent"/>, retiring and then disposing the current one.</summary>
    /// <param name="extent">The host's framebuffer size.</param>
    /// <returns>The new swap chain's formats.</returns>
    SwapChainFormats CreateSwapChain(Extent2D extent);

    /// <summary>Disposes the swap chain without replacing it.</summary>
    void DestroySwapChain();
}

/// <summary>
/// Decides when a renderer's swap chain and surface are destroyed and rebuilt, from the host's resize and
/// surface events, so the renderer never waits for a drawable area and never renders to a lost surface.
/// </summary>
sealed class SurfaceLifecycle : IDisposable
{
    readonly ISurfaceHost host;
    readonly ISwapChainTarget target;
    SwapChainFormats? formats;
    bool outOfDate = true;

    /// <summary>Starts tracking <paramref name="host"/>'s events for <paramref name="target"/>.</summary>
    /// <param name="host">The host whose surface is presented to.</param>
    /// <param name="target">The Vulkan objects to destroy and rebuild.</param>
    public SurfaceLifecycle(ISurfaceHost host, ISwapChainTarget target)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        host.Resized += OnResized;
        host.SurfaceLost += OnSurfaceLost;
        host.SurfaceRestored += OnSurfaceRestored;
    }

    /// <summary>Gets whether the surface is gone, so nothing is rendered until it is restored.</summary>
    public bool IsSurfaceLost { get; private set; }

    /// <summary>Raised after a rebuilt swap chain changed formats, so render passes and pipelines are rebuilt too.</summary>
    public event Action? FormatChanged;

    /// <summary>Marks the swap chain for rebuilding before the next frame, as after a suboptimal present.</summary>
    public void MarkOutOfDate() => outOfDate = true;

    /// <summary>Releases the device's work, the swap chain and then the surface; does nothing when already lost.</summary>
    public void OnSurfaceLost()
    {
        if (IsSurfaceLost)
        {
            return;
        }

        IsSurfaceLost = true;
        target.WaitIdle();
        if (target.HasSwapChain)
        {
            target.DestroySwapChain();
        }

        target.DestroySurface();
    }

    /// <summary>Recreates the surface; the swap chain follows on the next <see cref="TryPrepare"/>.</summary>
    public void OnSurfaceRestored()
    {
        if (!IsSurfaceLost)
        {
            return;
        }

        target.CreateSurface();
        IsSurfaceLost = false;
        outOfDate = true;
    }

    /// <summary>Replaces a surface the driver reported lost while the host still shows it.</summary>
    public void RebuildSurface()
    {
        OnSurfaceLost();
        OnSurfaceRestored();
    }

    /// <summary>
    /// Rebuilds the swap chain when it is missing or out of date. Returns at once, without waiting, while the
    /// surface is lost or has no drawable area.
    /// </summary>
    /// <returns>Whether a swap chain is ready to render to.</returns>
    public bool TryPrepare()
    {
        if (IsSurfaceLost)
        {
            return false;
        }

        if (!outOfDate && target.HasSwapChain)
        {
            return true;
        }

        if (DrawableExtent() is not { } extent)
        {
            return false;
        }

        Rebuild(extent);
        return true;
    }

    /// <summary>
    /// Applies the result of acquiring or presenting an image: out-of-date and suboptimal swap chains are rebuilt,
    /// and a surface the driver lost is replaced.
    /// </summary>
    /// <param name="result">The acquire or present result.</param>
    /// <param name="failure">The error message for any other failure.</param>
    /// <returns>Whether the frame can go on.</returns>
    /// <exception cref="VulkanException">The result is an error the lifecycle cannot recover from.</exception>
    public bool HandleResult(Result result, string failure)
    {
        switch (result)
        {
            case Result.Success:
                return true;
            case Result.ErrorOutOfDateKhr or Result.SuboptimalKhr:
                MarkOutOfDate();
                _ = TryPrepare();
                return false;
            case Result.ErrorSurfaceLostKhr:
                RebuildSurface();
                return false;
            default:
                throw new VulkanException(failure);
        }
    }

    /// <summary>Stops listening to the host.</summary>
    public void Dispose()
    {
        host.Resized -= OnResized;
        host.SurfaceLost -= OnSurfaceLost;
        host.SurfaceRestored -= OnSurfaceRestored;
    }

    Extent2D? DrawableExtent()
    {
        var size = host.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0 || !target.SurfaceHasArea)
        {
            return null;
        }

        return new Extent2D((uint)size.X, (uint)size.Y);
    }

    void Rebuild(Extent2D extent)
    {
        target.WaitIdle();
        var created = target.CreateSwapChain(extent);
        outOfDate = false;
        var changed = formats is { } previous && previous != created;
        formats = created;
        if (changed)
        {
            FormatChanged?.Invoke();
        }
    }

    void OnResized(Int2 size) => outOfDate = true;
}
