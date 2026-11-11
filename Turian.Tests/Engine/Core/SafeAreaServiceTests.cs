namespace Turian.Tests;

/// <summary>Tests the safe-area rectangle and its change notification with a fake platform provider.</summary>
public sealed class SafeAreaServiceTests
{
    /// <summary>Before any report the whole screen is safe.</summary>
    [Fact]
    public void BeforeFirstReportTheWholeScreenIsSafe()
    {
        var service = new SafeAreaService();

        Assert.Equal(new SafeAreaRect(0f, 0f, 1f, 1f), service.NormalizedRect);
        Assert.Equal(default, service.PixelRect);
    }

    /// <summary>Without insets, as on desktop, the safe area is the full window.</summary>
    [Fact]
    public void WithoutInsetsTheWindowIsSafe()
    {
        var service = new SafeAreaService();
        service.SetScreen(new Int2(800, 600));

        Assert.Equal(new SafeAreaRect(0, 0, 800, 600), service.PixelRect);
        Assert.Equal(new SafeAreaRect(0f, 0f, 1f, 1f), service.NormalizedRect);
    }

    /// <summary>Insets shrink the rectangle in pixels and in normalized coordinates.</summary>
    [Fact]
    public void InsetsShrinkTheRectangle()
    {
        var service = new SafeAreaService();
        service.SetScreen(new Int2(1000, 2000));
        service.SetInsets(new SafeAreaInsets(100, 200, 0, 300));

        Assert.Equal(new SafeAreaRect(100, 200, 900, 1500), service.PixelRect);
        Assert.Equal(new SafeAreaRect(0.1f, 0.1f, 0.9f, 0.75f), service.NormalizedRect);
    }

    /// <summary>Insets larger than the screen leave an empty rectangle instead of a negative one.</summary>
    [Fact]
    public void OversizedInsetsClamp()
    {
        var service = new SafeAreaService();
        service.SetScreen(new Int2(100, 100));
        service.SetInsets(new SafeAreaInsets(80, 0, 80, 0));

        Assert.Equal(new SafeAreaRect(80, 0, 0, 100), service.PixelRect);
    }

    /// <summary>Rotation and inset changes notify; repeating the same values, as every frame does, does not.</summary>
    [Fact]
    public void NotifiesOnlyOnChange()
    {
        var service = new SafeAreaService();
        var changes = 0;
        service.Changed += () => changes++;

        service.SetScreen(new Int2(800, 600));
        service.SetScreen(new Int2(800, 600));
        Assert.Equal(1, changes);

        service.SetScreen(new Int2(600, 800));
        service.SetInsets(new SafeAreaInsets(0, 10, 0, 0));
        service.SetInsets(new SafeAreaInsets(0, 10, 0, 0));
        Assert.Equal(3, changes);
    }
}
