using System.Collections.Concurrent;
using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Turian.Engine.Core;

namespace Turian.Android;

/// <summary>Marshals Android surface callbacks onto the dedicated rendering thread.</summary>
public sealed class AndroidSurfaceHost(Action<nint> releaseWindow) : ISurfaceHost
{
    /// <summary>How long a surface loss may block the Android UI thread, well under its unresponsive-app limit.</summary>
    public static readonly TimeSpan DefaultDetachTimeout = TimeSpan.FromSeconds(2);

    readonly ConcurrentQueue<Action> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly object gate = new();
    readonly List<TaskCompletionSource> releases = [];
    nint window;
    bool paused = true;
    bool active;
    bool closed;
    bool disposed;

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredInstanceExtensions { get; } = ["VK_KHR_surface", "VK_KHR_android_surface"];

    /// <inheritdoc />
    public Int2 FramebufferSize { get; private set; }

    /// <summary>Whether ticking and rendering must wait for a resumed activity and a drawable surface.</summary>
    public bool IsPaused => !active;

    /// <inheritdoc />
    public event Action<Int2>? Resized;

    /// <inheritdoc />
    public event Action? SurfaceLost;

    /// <inheritdoc />
    public event Action? SurfaceRestored;

    /// <inheritdoc />
    public event Action? Closing;

    /// <summary>Transfers an acquired native-window reference to the game thread.</summary>
    /// <param name="nativeWindow">A reference returned by ANativeWindow_fromSurface.</param>
    /// <param name="size">The initial drawable size.</param>
    public void Attach(nint nativeWindow, Int2 size)
    {
        if (nativeWindow == 0)
        {
            throw new ArgumentException("Android did not provide a native window.", nameof(nativeWindow));
        }
        lock (gate)
        {
            if (disposed)
            {
                releaseWindow(nativeWindow);
                return;
            }
            commands.Enqueue(() =>
            {
                LoseSurface();
                if (window != 0)
                {
                    releaseWindow(window);
                }
                window = nativeWindow;
                FramebufferSize = size;
                UpdateSurface();
                Resized?.Invoke(size);
            });
            wake.Set();
        }
    }

    /// <summary>The platform's latest safe-area insets, in surface pixels.</summary>
    public SafeAreaInsets Insets { get; private set; }

    /// <summary>Raised on the game thread when the platform's insets change.</summary>
    public event Action<SafeAreaInsets>? InsetsChanged;

    /// <summary>Queues a change of the platform's insets.</summary>
    /// <param name="insets">The insets in surface pixels.</param>
    public void SetInsets(SafeAreaInsets insets) => Post(() =>
    {
        Insets = insets;
        InsetsChanged?.Invoke(insets);
    });

    /// <summary>
    /// Queues the surface's release and waits for the game thread to do it. A game thread still in slow initialization
    /// holds no Vulkan surface, so the wait is bounded to keep the Android UI thread responsive.
    /// </summary>
    /// <param name="timeout">The longest time to block the calling thread; <see cref="DefaultDetachTimeout"/> when null.</param>
    /// <returns>True when the surface was released before the wait ended.</returns>
    public bool Detach(TimeSpan? timeout = null)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed)
            {
                return true;
            }
            releases.Add(completion);
            commands.Enqueue(() =>
            {
                LoseSurface();
                if (window != 0)
                {
                    releaseWindow(window);
                    window = 0;
                }
                FramebufferSize = default;
                completion.TrySetResult();
                lock (gate)
                {
                    releases.Remove(completion);
                }
            });
            wake.Set();
        }
        return completion.Task.Wait(timeout ?? DefaultDetachTimeout);
    }

    /// <summary>Queues pause/resume without blocking the Android UI thread.</summary>
    /// <param name="value">Whether the activity is paused.</param>
    public void SetPaused(bool value) => Post(() => { paused = value; UpdateSurface(); });

    /// <summary>Queues a drawable-size change.</summary>
    /// <param name="size">The new size in pixels.</param>
    public void Resize(Int2 size) => Post(() =>
    {
        FramebufferSize = size;
        UpdateSurface();
        Resized?.Invoke(size);
    });

    /// <summary>Queues input work so it is consumed between frames.</summary>
    /// <param name="action">Work executed on the game thread.</param>
    public void Post(Action action)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            commands.Enqueue(action);
            wake.Set();
        }
    }

    /// <summary>Waits for the first resumed drawable surface before creating the renderer.</summary>
    /// <param name="cancellationToken">Ends startup when the application quits before a surface exists.</param>
    /// <returns>False when the activity closed or startup was cancelled before a surface became available.</returns>
    public bool WaitForSurface(CancellationToken cancellationToken = default)
    {
        WaitHandle[] signals = [wake, cancellationToken.WaitHandle];
        while (!closed && !cancellationToken.IsCancellationRequested)
        {
            PumpEvents();
            if (closed || cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (active)
            {
                return true;
            }
            WaitHandle.WaitAny(signals);
        }
        return false;
    }

    /// <summary>Runs queued UI callbacks on the game thread.</summary>
    public void PumpEvents()
    {
        while (commands.TryDequeue(out var action))
        {
            action();
        }
    }

    /// <inheritdoc />
    public void Run(Action<double> frame)
    {
        var previous = Stopwatch.GetTimestamp();
        var wasPaused = true;
        while (!closed)
        {
            PumpEvents();
            if (closed)
            {
                break;
            }
            var now = Stopwatch.GetTimestamp();
            var delta = wasPaused ? 0 : Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
            previous = now;
            frame(delta);
            wasPaused = !active;
            if (wasPaused && !closed)
            {
                wake.WaitOne(50);
            }
        }
        Closing?.Invoke();
    }

    /// <inheritdoc />
    public void Close() => Post(() => closed = true);

    /// <inheritdoc />
    public IInputContext CreateInput() =>
        throw new NotSupportedException("Android input is delivered by its activity to BufferedInputSource.");

    /// <inheritdoc />
    public unsafe SurfaceKHR CreateSurface(Vk vk, Instance instance)
    {
        if (window == 0 || !vk.TryGetInstanceExtension(instance, out KhrAndroidSurface extension))
        {
            throw new InvalidOperationException("No Android Vulkan surface is available.");
        }
        using (extension)
        {
            var info = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)window,
            };
            var result = extension.CreateAndroidSurface(instance, in info, null, out var surface);
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkCreateAndroidSurfaceKHR failed: {result}.");
            }
            return surface;
        }
    }

    /// <inheritdoc />
    public unsafe void DestroySurface(Vk vk, Instance instance, SurfaceKHR surface)
    {
        if (vk.TryGetInstanceExtension(instance, out KhrSurface extension))
        {
            using (extension)
            {
                extension.DestroySurface(instance, surface, null);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        try
        {
            PumpEvents();
            LoseSurface();
        }
        finally
        {
            if (window != 0)
            {
                releaseWindow(window);
                window = 0;
            }
            lock (gate)
            {
                foreach (var completion in releases)
                {
                    completion.TrySetResult();
                }
                releases.Clear();
            }
            wake.Dispose();
        }
    }

    void LoseSurface()
    {
        if (active)
        {
            active = false;
            SurfaceLost?.Invoke();
        }
    }

    void UpdateSurface()
    {
        var ready = !paused && window != 0 && FramebufferSize.X > 0 && FramebufferSize.Y > 0;
        if (!ready)
        {
            LoseSurface();
        }
        else if (!active)
        {
            active = true;
            SurfaceRestored?.Invoke();
        }
    }
}
