using Silk.NET.Windowing;

namespace Turian.Tests;

/// <summary>Checks runtime statistics using a substituted window without starting a desktop application.</summary>
public sealed class WindowManagerStatsTests
{
    /// <summary>The title reports frame rate and the latest view's submitted and culled counts when stats are enabled.</summary>
    [Fact]
    public void TitleIncludesCullingCounts()
    {
        var window = Substitute.For<IWindow>();
        ((IView)window).Size.Returns(new Silk.NET.Maths.Vector2D<int>(1280, 720));
        var manager = (WindowManager)RuntimeHelpers.GetUninitializedObject(typeof(WindowManager));
        typeof(WindowManager).GetProperty(nameof(WindowManager.Window))!.SetValue(manager, window);
        manager.Title = "Benchmark";
        manager.CullingStats = new RenderCullingStats(691, 900);
        manager.Initialize();
        window.Update += Raise.Event<Action<double>>(1.0 / 60.0);
        Assert.Equal("Benchmark", window.Title);

        manager.ShowStats = true;
        typeof(WindowManager).GetField("fpsLastUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, 0L);
        window.Update += Raise.Event<Action<double>>(1.0 / 60.0);
        Assert.Contains("Benchmark | 1280x720", window.Title);
        Assert.Contains("fps | 691 drawn, 900/1591 culled", window.Title);
        manager.Dispose();
    }
}
