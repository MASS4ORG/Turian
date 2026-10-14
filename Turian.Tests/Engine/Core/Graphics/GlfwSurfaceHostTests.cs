using Silk.NET.Windowing;

namespace Turian.Tests;

/// <summary>Checks the GLFW surface host using a substituted window without starting a desktop application.</summary>
public sealed class GlfwSurfaceHostTests
{
    static GlfwSurfaceHost Host(IWindow window)
    {
        var host = (GlfwSurfaceHost)RuntimeHelpers.GetUninitializedObject(typeof(GlfwSurfaceHost));
        typeof(GlfwSurfaceHost).GetProperty(nameof(GlfwSurfaceHost.Window))!.SetValue(host, window);
        typeof(GlfwSurfaceHost).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, "Turian");
        return host;
    }

    /// <summary>The title reports frame rate and the latest view's submitted and culled counts when stats are enabled.</summary>
    [Fact]
    public void TitleIncludesCullingCounts()
    {
        var window = Substitute.For<IWindow>();
        ((IView)window).Size.Returns(new Silk.NET.Maths.Vector2D<int>(1280, 720));
        var manager = Host(window);
        manager.Title = "Benchmark";
        manager.CullingStats = new RenderCullingStats(691, 900);
        manager.Initialize();
        window.Update += Raise.Event<Action<double>>(1.0 / 60.0);
        Assert.Equal("Benchmark", window.Title);

        manager.ShowStats = true;
        typeof(GlfwSurfaceHost).GetField("fpsLastUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, 0L);
        window.Update += Raise.Event<Action<double>>(1.0 / 60.0);
        Assert.Contains("Benchmark | 1280x720", window.Title);
        Assert.Contains("fps | 691 drawn, 900/1591 culled", window.Title);
        manager.Dispose();
    }

    /// <summary>Window resizes and close requests reach the host's events, and the size reads the framebuffer.</summary>
    [Fact]
    public void WindowEventsAreForwarded()
    {
        var window = Substitute.For<IWindow>();
        window.FramebufferSize.Returns(new Silk.NET.Maths.Vector2D<int>(640, 480));
        var host = Host(window);
        var sizes = new List<Int2>();
        var closings = 0;
        host.Resized += sizes.Add;
        host.Closing += () => closings++;
        host.Initialize();

        window.FramebufferResize += Raise.Event<Action<Silk.NET.Maths.Vector2D<int>>>(
            new Silk.NET.Maths.Vector2D<int>(800, 600));
        window.Closing += Raise.Event<Action>();

        Assert.Equal([new Int2(800, 600)], sizes);
        Assert.Equal(1, closings);
        Assert.Equal(new Int2(640, 480), host.FramebufferSize);
        host.Close();
        window.Received(1).Close();
        host.Dispose();
    }

    /// <summary>Minimizing reports a lost surface once, and leaving the minimized state reports it restored.</summary>
    [Fact]
    public void MinimizingLosesAndRestoresTheSurface()
    {
        var window = Substitute.For<IWindow>();
        var host = Host(window);
        var events = new List<string>();
        host.SurfaceLost += () => events.Add("lost");
        host.SurfaceRestored += () => events.Add("restored");
        host.Initialize();

        window.StateChanged += Raise.Event<Action<WindowState>>(WindowState.Normal);
        window.StateChanged += Raise.Event<Action<WindowState>>(WindowState.Minimized);
        Assert.True(host.IsSurfaceLost);
        Assert.True(window.IsEventDriven);
        window.StateChanged += Raise.Event<Action<WindowState>>(WindowState.Minimized);
        window.StateChanged += Raise.Event<Action<WindowState>>(WindowState.Maximized);

        Assert.Equal(["lost", "restored"], events);
        Assert.False(host.IsSurfaceLost);
        Assert.False(window.IsEventDriven);
        host.Dispose();
    }

    /// <summary>The frame pump renders through the given callback only while it runs.</summary>
    [Fact]
    public void RunPumpsFramesThroughTheWindow()
    {
        var window = Substitute.For<IWindow>();
        var host = Host(window);
        var frames = new List<double>();
        window.When(w => w.Run(Arg.Any<Action>())).Do(_ => window.Render += Raise.Event<Action<double>>(0.25));

        host.Run(frames.Add);
        window.Render += Raise.Event<Action<double>>(0.5);

        Assert.Equal([0.25], frames);
    }
}
