namespace Turian.Engine.UI;

/// <summary>
/// Owns a CPU raster <see cref="SKSurface"/> and turns one Guinevere draw pass into a
/// tightly-packed RGBA8 pixel block. This is the graphics-API-agnostic half of the UI
/// render backend: it has no Vulkan dependency and is unit-testable on its own.
/// </summary>
/// <remarks>
/// The surface is <see cref="SKColorType.Rgba8888"/> / <see cref="SKAlphaType.Unpremul"/>
/// so the bytes map directly onto <c>VK_FORMAT_R8G8B8A8_*</c> and the compositor can blend
/// with straight <c>src-alpha / one-minus-src-alpha</c>, matching every stock Guinevere
/// backend. The canvas is cleared to fully transparent each frame because the UI is
/// composited over an already-rendered scene rather than owning the whole framebuffer.
/// </remarks>
public sealed class SkiaFrameRasterizer : IDisposable
{
    SKSurface? surface;
    byte[] pixels = [];
    bool disposed;

    /// <summary>Target width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Target height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Creates a rasterizer with an initial target size.</summary>
    /// <param name="width">Initial width in pixels, greater than zero.</param>
    /// <param name="height">Initial height in pixels, greater than zero.</param>
    public SkiaFrameRasterizer(int width, int height) => Resize(width, height);

    /// <summary>
    /// Resizes the raster target. Does nothing when the size is unchanged.
    /// </summary>
    /// <param name="width">New width in pixels, greater than zero.</param>
    /// <param name="height">New height in pixels, greater than zero.</param>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == Width && height == Height && surface is not null) return;

        surface?.Dispose();
        surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Width = width;
        Height = height;
        pixels = new byte[width * height * 4];
    }

    /// <summary>
    /// Clears the canvas to transparent, invokes <paramref name="draw"/> against it, and returns
    /// the flushed frame as tightly-packed top-left-origin RGBA8 pixels (length Width*Height*4).
    /// The span is owned by this instance and overwritten by the next call.
    /// </summary>
    /// <param name="draw">Receives the surface canvas for one Guinevere render pass.</param>
    public ReadOnlySpan<byte> Render(Action<SKCanvas> draw)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(draw);

        var targetSurface = surface ?? throw new InvalidOperationException("Rasterizer has no surface");

        var canvas = targetSurface.Canvas;

        // SKCanvas transform state is not reset by Clear(). Guinevere's canvasScale is applied
        // inside `draw` every frame, so without resetting to identity first it compounds frame
        // over frame — the "UI zooms away and vanishes" bug at any non-1.0 UI scale.
        canvas.ResetMatrix();
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        canvas.Flush();

        DumpIfRequested(targetSurface);

        using var pixmap = targetSurface.PeekPixels()
                           ?? throw new InvalidOperationException("SKSurface.PeekPixels returned null");

        var rowBytes = pixmap.RowBytes;
        var tightRow = Width * 4;
        var src = pixmap.GetPixelSpan();

        if (rowBytes == tightRow)
        {
            src[..pixels.Length].CopyTo(pixels);
        }
        else
        {
            // Raster surfaces are usually tightly packed, but the color type's alignment can pad
            // the stride; copy row by row when it does.
            for (var y = 0; y < Height; y++)
                src.Slice(y * rowBytes, tightRow).CopyTo(pixels.AsSpan(y * tightRow));
        }

        return pixels;
    }

    int dumpCount;

    // Diagnostic: set TURIAN_UI_DUMP=<dir> to write the first few rasterized UI frames as PNGs, from
    // whatever process is compositing (headless CLI or the Studio). Confirms the UI is actually
    // being drawn independent of how it is later composited into the viewport.
    void DumpIfRequested(SKSurface dumpSurface)
    {
        if (dumpCount >= 3) return;
        var dir = Environment.GetEnvironmentVariable("TURIAN_UI_DUMP");
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"ui-{DateTime.Now:HHmmss}-{Width}x{Height}-{dumpCount}.png");
            using var image = dumpSurface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
            dumpCount++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort diagnostic
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        surface?.Dispose();
        surface = null;
    }
}
