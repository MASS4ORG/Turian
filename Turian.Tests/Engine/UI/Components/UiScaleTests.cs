namespace Turian.Tests;

/// <summary>Tests for <see cref="UiScale.Resolve"/> — the framebuffer → logical-size math.</summary>
public sealed class UiScaleTests
{
    /// <summary>Constant pixel size is 1:1 with no canvas scaling.</summary>
    [Fact]
    public void ConstantPixelSize_IsOneToOne()
    {
        var s = UiScale.Resolve(1280, 720, UiScaleMode.ConstantPixelSize);

        Assert.Equal(1280, s.PixelWidth);
        Assert.Equal(720, s.PixelHeight);
        Assert.Equal(1f, s.CanvasScale);
    }

    /// <summary>At exactly the reference resolution the scale factor is 1.</summary>
    [Fact]
    public void ScaleWithScreenSize_AtReference_IsUnitScale()
    {
        var s = UiScale.Resolve(1920, 1080, UiScaleMode.ScaleWithScreenSize);

        Assert.Equal(1f, s.CanvasScale, 3);
    }

    /// <summary>Doubling both dimensions doubles the scale; halving halves it.</summary>
    [Theory]
    [InlineData(3840, 2160, 2f)]
    [InlineData(960, 540, 0.5f)]
    public void ScaleWithScreenSize_TracksResolution(int w, int h, float expected)
    {
        var s = UiScale.Resolve(w, h, UiScaleMode.ScaleWithScreenSize);

        Assert.Equal(expected, s.CanvasScale, 3);
        Assert.Equal(w, s.PixelWidth);
        Assert.Equal(h, s.PixelHeight);
    }

    /// <summary>Constant physical size falls back to 1:1 until display DPI is available.</summary>
    [Fact]
    public void ConstantPhysicalSize_FallsBackToOneToOne()
    {
        var s = UiScale.Resolve(1280, 720, UiScaleMode.ConstantPhysicalSize);

        Assert.Equal(1f, s.CanvasScale);
    }

    /// <summary>Non-positive dimensions are rejected.</summary>
    [Fact]
    public void Resolve_RejectsNonPositiveFramebuffer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiScale.Resolve(0, 100, UiScaleMode.ConstantPixelSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiScale.Resolve(100, -1, UiScaleMode.ConstantPixelSize));
    }
}
