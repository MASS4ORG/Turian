using Turian.Android;

namespace Turian.Tests;

/// <summary>Native-window ownership and lifecycle ordering without a Vulkan device.</summary>
public sealed class AndroidSurfaceHostTests
{
    /// <summary>Surface callbacks are marshaled and release follows surface-loss notification.</summary>
    [Fact]
    public async Task DetachWaitsForRenderThreadRelease()
    {
        List<string> events = [];
        using var host = new AndroidSurfaceHost(window => events.Add($"release:{window}"));
        host.SurfaceRestored += () => events.Add("restored");
        host.SurfaceLost += () => events.Add("lost");
        host.Attach(42, new Int2(800, 600));
        host.SetPaused(false);
        Assert.Empty(events);
        host.PumpEvents();
        Assert.False(host.IsPaused);
        Assert.Equal(["restored"], events);

        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detach = Task.Run(() => { queued.SetResult(); host.Detach(); }, TestContext.Current.CancellationToken);
        await queued.Task;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!detach.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The render-thread release did not complete.");
            host.PumpEvents();
            await Task.Yield();
        }
        await detach;
        Assert.Equal(["restored", "lost", "release:42"], events);
        Assert.True(host.IsPaused);
        Assert.Equal(default, host.FramebufferSize);
    }

    /// <summary>A game thread busy initializing cannot hold the Android UI thread past the bounded wait.</summary>
    [Fact]
    public void DetachReturnsWhenTheGameThreadIsBusy()
    {
        List<nint> released = [];
        using var host = new AndroidSurfaceHost(released.Add);
        host.Attach(42, new Int2(800, 600));
        host.PumpEvents();

        Assert.False(host.Detach(TimeSpan.FromMilliseconds(20)));
        Assert.Empty(released);

        host.PumpEvents();
        Assert.Equal([42], released);
    }

    /// <summary>Insets change on the game thread, in order with the rest of the surface's events.</summary>
    [Fact]
    public void InsetsAreMarshaledToTheGameThread()
    {
        using var host = new AndroidSurfaceHost(static _ => { });
        SafeAreaInsets? seen = null;
        host.InsetsChanged += insets => seen = insets;
        var expected = new SafeAreaInsets(1, 2, 3, 4);

        host.SetInsets(expected);
        Assert.Null(seen);

        host.PumpEvents();
        Assert.Equal(expected, seen);
        Assert.Equal(expected, host.Insets);
    }

    /// <summary>Pause releases the Vulkan surface while retaining the native-window reference for resume.</summary>
    [Fact]
    public void PauseResumeAndResizeKeepWindowOwnership()
    {
        List<nint> releases = [];
        using var host = new AndroidSurfaceHost(releases.Add);
        var restored = 0;
        var lost = 0;
        host.SurfaceRestored += () => restored++;
        host.SurfaceLost += () => lost++;
        host.Attach(7, new Int2(800, 600));
        host.SetPaused(false);
        host.PumpEvents();
        host.SetPaused(true);
        host.PumpEvents();
        Assert.True(host.IsPaused);
        Assert.Empty(releases);
        host.SetPaused(false);
        host.Resize(new Int2(600, 800));
        host.PumpEvents();
        Assert.Equal(new Int2(600, 800), host.FramebufferSize);
        Assert.Equal(2, restored);
        Assert.Equal(1, lost);
    }

    /// <summary>Shutdown releases a window queued before startup and accepts late Android callbacks.</summary>
    [Fact]
    public void ShutdownReleasesQueuedWindows()
    {
        List<nint> released = [];
        var host = new AndroidSurfaceHost(released.Add);
        host.Attach(1, new Int2(1, 1));
        host.Attach(2, new Int2(1, 1));
        host.Dispose();
        host.Attach(3, new Int2(1, 1));
        host.Detach();
        host.Dispose();
        Assert.Equal([1, 2, 3], released);
    }

    /// <summary>Quitting before the first surface does not leave the game thread waiting for a view.</summary>
    [Fact]
    public void CancellationWithoutASurfaceUnblocksStartup()
    {
        using var host = new AndroidSurfaceHost(static _ => { });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(host.WaitForSurface(cancellation.Token));
    }

    /// <summary>The first and resumed frames receive no elapsed background time.</summary>
    [Fact]
    public void ResumeResetsFrameElapsedTime()
    {
        using var host = new AndroidSurfaceHost(static _ => { });
        host.Attach(1, new Int2(100, 100));
        host.SetPaused(false);
        List<double> deltas = [];
        host.Run(delta =>
        {
            deltas.Add(delta);
            if (deltas.Count == 1)
            {
                host.SetPaused(true);
            }
            else if (deltas.Count == 2)
            {
                host.SetPaused(false);
            }
            else
            {
                host.Close();
            }
        });
        Assert.Equal(3, deltas.Count);
        Assert.Equal(0, deltas[0]);
        Assert.Equal(0, deltas[2]);
    }

    /// <summary>Activity destruction before its first surface unblocks startup.</summary>
    [Fact]
    public void ClosingWithoutASurfaceUnblocksStartup()
    {
        using var host = new AndroidSurfaceHost(static _ => { });
        host.Close();
        Assert.False(host.WaitForSurface(TestContext.Current.CancellationToken));
    }
}
