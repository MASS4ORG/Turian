namespace Turian.Tests;

/// <summary>The swap chain and surface lifecycle, driven by a fake surface host without a window or GPU.</summary>
public sealed class SurfaceLifecycleTests
{
    /// <summary>A surface host whose size and events the test controls, counting how often its size is read.</summary>
    sealed class FakeSurfaceHost : ISurfaceHost
    {
        Int2 size = new(640, 480);

        public int SizeReads { get; private set; }

        public void SetSize(Int2 value) => size = value;

        public IReadOnlyList<string> RequiredInstanceExtensions => [];

        public Int2 FramebufferSize
        {
            get
            {
                SizeReads++;
                return size;
            }
        }

        public event Action<Int2>? Resized;

        public event Action? SurfaceLost;

        public event Action? SurfaceRestored;

        public event Action? Closing
        {
            add { }
            remove { }
        }

        public int Subscribers => (Resized?.GetInvocationList().Length ?? 0)
            + (SurfaceLost?.GetInvocationList().Length ?? 0) + (SurfaceRestored?.GetInvocationList().Length ?? 0);

        public void Resize(Int2 newSize)
        {
            size = newSize;
            Resized?.Invoke(newSize);
        }

        public void Lose() => SurfaceLost?.Invoke();

        public void Restore() => SurfaceRestored?.Invoke();

        public SurfaceKHR CreateSurface(Vk vk, Instance instance) => default;

        public void DestroySurface(Vk vk, Instance instance, SurfaceKHR surface) { }

        public void Run(Action<double> frame) => frame(0);

        public void Close() { }

        public Silk.NET.Input.IInputContext CreateInput() => null!;

        public void Dispose() { }
    }

    /// <summary>Records every call the lifecycle makes, in order.</summary>
    sealed class RecordingTarget : ISwapChainTarget
    {
        public List<string> Calls { get; } = [];

        public SwapChainFormats Formats { get; set; } = new(Format.B8G8R8A8Srgb, Format.D32Sfloat);

        public bool SurfaceHasArea { get; set; } = true;

        public bool HasSwapChain { get; private set; }

        public void WaitIdle() => Calls.Add("idle");

        public void CreateSurface() => Calls.Add("create surface");

        public void DestroySurface() => Calls.Add("destroy surface");

        public SwapChainFormats CreateSwapChain(Extent2D extent)
        {
            Calls.Add($"swapchain {extent.Width}x{extent.Height}");
            HasSwapChain = true;
            return Formats;
        }

        public void DestroySwapChain()
        {
            Calls.Add("destroy swapchain");
            HasSwapChain = false;
        }
    }

    readonly FakeSurfaceHost host = new();
    readonly RecordingTarget target = new();

    SurfaceLifecycle Lifecycle() => new(host, target);

    /// <summary>The first frame builds the swap chain once; later frames reuse it.</summary>
    [Fact]
    public void TheSwapChainIsBuiltOnceUntilSomethingChanges()
    {
        var lifecycle = Lifecycle();

        Assert.True(lifecycle.TryPrepare());
        Assert.True(lifecycle.TryPrepare());

        Assert.Equal(["idle", "swapchain 640x480"], target.Calls);
    }

    /// <summary>
    /// Losing the surface waits for the device, then destroys the swap chain before the surface, and frames are
    /// skipped until it is restored; repeated loss notifications do nothing more.
    /// </summary>
    [Fact]
    public void LosingTheSurfaceReleasesTheSwapChainThenTheSurface()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryPrepare());
        target.Calls.Clear();

        host.Lose();
        host.Lose();

        Assert.True(lifecycle.IsSurfaceLost);
        Assert.Equal(["idle", "destroy swapchain", "destroy surface"], target.Calls);
        Assert.False(lifecycle.TryPrepare());
        Assert.Equal(3, target.Calls.Count);
    }

    /// <summary>Restoring recreates the surface at once and the swap chain on the next frame.</summary>
    [Fact]
    public void RestoringRecreatesTheSurfaceAndThenTheSwapChain()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryPrepare());
        host.Lose();
        target.Calls.Clear();

        host.Restore();
        host.Restore();
        Assert.Equal(["create surface"], target.Calls);

        Assert.True(lifecycle.TryPrepare());
        Assert.False(lifecycle.IsSurfaceLost);
        Assert.Equal(["create surface", "idle", "swapchain 640x480"], target.Calls);
    }

    /// <summary>A surface lost before any swap chain existed destroys only the surface.</summary>
    [Fact]
    public void LosingBeforeTheFirstFrameDestroysOnlyTheSurface()
    {
        var lifecycle = Lifecycle();

        lifecycle.OnSurfaceLost();

        Assert.Equal(["idle", "destroy surface"], target.Calls);
    }

    /// <summary>
    /// A zero-sized framebuffer skips the frame after one size read, without waiting or building anything, and the
    /// swap chain is built once the size returns.
    /// </summary>
    [Fact]
    public void AZeroSizeSkipsTheFrameWithoutWaiting()
    {
        var lifecycle = Lifecycle();
        host.SetSize(new Int2(0, 0));

        Assert.False(lifecycle.TryPrepare());
        Assert.Equal(1, host.SizeReads);
        Assert.Empty(target.Calls);

        host.SetSize(new Int2(0, 300));
        Assert.False(lifecycle.TryPrepare());
        Assert.Equal(2, host.SizeReads);

        host.SetSize(new Int2(400, 300));
        Assert.True(lifecycle.TryPrepare());
        Assert.Equal(["idle", "swapchain 400x300"], target.Calls);
    }

    /// <summary>A surface reporting no drawable area yet skips the frame and is retried on the next one.</summary>
    [Fact]
    public void ASurfaceWithoutAreaIsRetriedNextFrame()
    {
        var lifecycle = Lifecycle();
        target.SurfaceHasArea = false;

        Assert.False(lifecycle.TryPrepare());
        Assert.False(target.HasSwapChain);

        target.SurfaceHasArea = true;
        Assert.True(lifecycle.TryPrepare());
        Assert.Equal(["idle", "swapchain 640x480"], target.Calls);
    }

    /// <summary>
    /// Acquire and present results go on after success, rebuild the swap chain when out of date or suboptimal,
    /// replace a lost surface, and throw on anything else.
    /// </summary>
    [Fact]
    public void SwapChainResultsMapToLifecycleSteps()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryPrepare());
        target.Calls.Clear();

        Assert.True(lifecycle.HandleResult(Result.Success, "unused"));
        Assert.Empty(target.Calls);

        Assert.False(lifecycle.HandleResult(Result.ErrorOutOfDateKhr, "unused"));
        Assert.False(lifecycle.HandleResult(Result.SuboptimalKhr, "unused"));
        Assert.Equal(["idle", "swapchain 640x480", "idle", "swapchain 640x480"], target.Calls);
        target.Calls.Clear();

        Assert.False(lifecycle.HandleResult(Result.ErrorSurfaceLostKhr, "unused"));
        Assert.Equal(["idle", "destroy swapchain", "destroy surface", "create surface"], target.Calls);

        var error = Assert.Throws<VulkanException>(() => lifecycle.HandleResult(Result.ErrorDeviceLost, "gone"));
        Assert.Contains("gone", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The host's resize event, and an out-of-date result, rebuild the swap chain at the new size.</summary>
    [Fact]
    public void ResizesAndOutOfDateResultsRebuildTheSwapChain()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryPrepare());
        target.Calls.Clear();

        host.Resize(new Int2(800, 600));
        Assert.True(lifecycle.TryPrepare());
        lifecycle.MarkOutOfDate();
        Assert.True(lifecycle.TryPrepare());

        Assert.Equal(["idle", "swapchain 800x600", "idle", "swapchain 800x600"], target.Calls);
    }

    /// <summary>Only a rebuilt swap chain whose formats differ from the previous one asks for new pipelines.</summary>
    [Fact]
    public void FormatChangesAreReported()
    {
        var lifecycle = Lifecycle();
        var changes = 0;
        lifecycle.FormatChanged += () => changes++;

        Assert.True(lifecycle.TryPrepare());
        lifecycle.MarkOutOfDate();
        Assert.True(lifecycle.TryPrepare());
        Assert.Equal(0, changes);

        host.Lose();
        target.Formats = new SwapChainFormats(Format.R8G8B8A8Srgb, Format.D32Sfloat);
        host.Restore();
        Assert.True(lifecycle.TryPrepare());

        Assert.Equal(1, changes);
    }

    /// <summary>A surface the driver lost is destroyed and recreated in place.</summary>
    [Fact]
    public void RebuildingTheSurfaceDestroysThenRecreatesIt()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryPrepare());
        target.Calls.Clear();

        lifecycle.RebuildSurface();

        Assert.False(lifecycle.IsSurfaceLost);
        Assert.Equal(["idle", "destroy swapchain", "destroy surface", "create surface"], target.Calls);
        Assert.True(lifecycle.TryPrepare());
    }

    /// <summary>A hundred lose and restore cycles leave exactly one swap chain and one surface, in balance.</summary>
    [Fact]
    public void RepeatedCyclesStayBalanced()
    {
        var lifecycle = Lifecycle();

        for (var i = 0; i < 100; i++)
        {
            Assert.True(lifecycle.TryPrepare());
            host.Lose();
            Assert.False(lifecycle.TryPrepare());
            host.Restore();
        }

        Assert.True(lifecycle.TryPrepare());
        Assert.Equal(100, target.Calls.Count(static c => c == "destroy surface"));
        Assert.Equal(100, target.Calls.Count(static c => c == "create surface"));
        Assert.Equal(101, target.Calls.Count(static c => c.StartsWith("swapchain ", StringComparison.Ordinal)));
        Assert.Equal(100, target.Calls.Count(static c => c == "destroy swapchain"));
    }

    /// <summary>Disposing stops following the host, and the constructor rejects missing arguments.</summary>
    [Fact]
    public void DisposingUnsubscribesFromTheHost()
    {
        var lifecycle = Lifecycle();
        Assert.Equal(3, host.Subscribers);

        lifecycle.Dispose();
        host.Lose();

        Assert.Equal(0, host.Subscribers);
        Assert.Empty(target.Calls);
        Assert.Throws<ArgumentNullException>(() => new SurfaceLifecycle(null!, target));
        Assert.Throws<ArgumentNullException>(() => new SurfaceLifecycle(host, null!));
    }
}
