namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="SkiaFrameRasterizer"/> — the CPU-Skia, no-Vulkan half of the UI
/// render backend. Verifies size, transparent clear, RGBA byte order and packing.
/// </summary>
public sealed class SkiaFrameRasterizerTests
{
    /// <summary>The returned block is tightly packed top-left-origin RGBA8 at the target size.</summary>
    [Fact]
    public void Render_ProducesTightlyPackedRgbaOfTargetSize()
    {
        using var r = new SkiaFrameRasterizer(8, 4);

        var pixels = r.Render(_ => { });

        Assert.Equal(8, r.Width);
        Assert.Equal(4, r.Height);
        Assert.Equal(8 * 4 * 4, pixels.Length);
    }

    /// <summary>With nothing drawn, every pixel is fully transparent (the compositor sees nothing).</summary>
    [Fact]
    public void Render_WithNoDrawing_IsFullyTransparent()
    {
        using var r = new SkiaFrameRasterizer(4, 4);

        var pixels = r.Render(_ => { });

        foreach (var b in pixels)
            Assert.Equal(0, b);
    }

    /// <summary>A solid red fill reads back as R=255, G=0, B=0, A=255 in that byte order.</summary>
    [Fact]
    public void Render_SolidRed_HasExpectedRgbaByteOrder()
    {
        using var r = new SkiaFrameRasterizer(2, 2);

        var pixels = r.Render(canvas =>
        {
            using (var paint = new SKPaint())
            {
                paint.Color = SKColors.Red;
                canvas.DrawRect(new SKRect(0, 0, 2, 2), paint);
            }
        });

        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 0]); // R
            Assert.Equal(0, pixels[i + 1]);   // G
            Assert.Equal(0, pixels[i + 2]);   // B
            Assert.Equal(255, pixels[i + 3]); // A
        }
    }

    /// <summary>Left half red, right half blue: row layout is left-to-right, top-to-bottom.</summary>
    [Fact]
    public void Render_SplitFill_PlacesPixelsByRowMajorOrder()
    {
        using var r = new SkiaFrameRasterizer(4, 2);

        var pixels = r.Render(canvas =>
        {
            using (var red = new SKPaint())
            using (var blue = new SKPaint())
            {
                red.Color = SKColors.Red;
                blue.Color = SKColors.Blue;
                canvas.DrawRect(new SKRect(0, 0, 2, 2), red);
                canvas.DrawRect(new SKRect(2, 0, 4, 2), blue);
            }
        });

        // pixel (0,0) red, pixel (3,0) blue
        Assert.Equal(255, pixels[0]);                 // (0,0) R
        Assert.Equal(0, pixels[2]);                   // (0,0) B
        var lastInRow0 = 3 * 4;
        Assert.Equal(0, pixels[lastInRow0 + 0]);      // (3,0) R
        Assert.Equal(255, pixels[lastInRow0 + 2]);    // (3,0) B
    }

    /// <summary>Resizing changes the reported size and the output length, and is idempotent.</summary>
    [Fact]
    public void Resize_ChangesSizeAndOutputLength()
    {
        using var r = new SkiaFrameRasterizer(4, 4);
        Assert.Equal(4 * 4 * 4, r.Render(_ => { }).Length);

        r.Resize(6, 3);
        Assert.Equal((6, 3), (r.Width, r.Height));
        Assert.Equal(6 * 3 * 4, r.Render(_ => { }).Length);

        r.Resize(6, 3); // no-op
        Assert.Equal(6 * 3 * 4, r.Render(_ => { }).Length);
    }

    /// <summary>Zero or negative dimensions are rejected.</summary>
    [Fact]
    public void Resize_RejectsNonPositiveDimensions()
    {
        using var r = new SkiaFrameRasterizer(4, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => r.Resize(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Resize(4, -1));
    }

    /// <summary>Using a disposed rasterizer throws rather than crashing in native code.</summary>
    [Fact]
    public void Render_AfterDispose_Throws()
    {
        var r = new SkiaFrameRasterizer(4, 4);
        r.Dispose();

        Assert.Throws<ObjectDisposedException>(() => r.Render(_ => { }));
    }

    /// <summary>
    /// The canvas transform is reset to identity each frame, so a scale a previous frame's draw
    /// applied does not compound (the "UI zooms away and vanishes" regression).
    /// </summary>
    [Fact]
    public void Render_ResetsCanvasTransformBetweenFrames()
    {
        using var r = new SkiaFrameRasterizer(16, 16);

        void Draw(SKCanvas canvas)
        {
            canvas.Scale(0.5f);                 // same as Guinevere's per-frame canvasScale
            using (var paint = new SKPaint())
            {
                paint.Color = SKColors.Red;
                canvas.DrawRect(new SKRect(0, 0, 32, 32), paint); // fills the whole 16x16 after 0.5 scale
            }
        }

        _ = r.Render(Draw);
        var second = r.Render(Draw).ToArray();

        // If the 0.5 scale compounded, frame 2 would only cover the top-left 8x8; the far corner
        // (15,15) would be transparent. With the reset it stays fully covered.
        var corner = ((15 * 16) + 15) * 4;
        Assert.Equal(255, second[corner + 3]);
        Assert.Equal(255, second[corner + 0]);
    }
}
