namespace Turian.Engine.UI;

/// <summary>Draws HUD lines with reusable character storage and cached glyphs.</summary>
sealed class FrameStatisticsDrawing(FrameStatisticsHudComponent component) : IDrawable, IDisposable
{
    readonly char[] text = new char[192];
    readonly SKPaint background = new() { Color = new SKColor(20, 24, 30, 235) };
    readonly SKPaint ink = new() { IsAntialias = true, Color = SKColors.White };
    readonly SKFont font = new(SKTypeface.Default);
    readonly SKTextBlob?[] glyphs = new SKTextBlob?[95];
    readonly float[] advances = new float[95];

    /// <inheritdoc />
    public SKPaint Paint => ink;

    /// <inheritdoc />
    public void Render(Gui gui, LayoutNode node, SKCanvas canvas)
    {
        PrepareGlyphs(Math.Max(1, component.FontSize));
        canvas.DrawRoundRect(new SKRect(node.Rect.X, node.Rect.Y, node.Rect.X + node.Rect.W,
            node.Rect.Y + node.Rect.H), 4, 4, background);
        for (var line = 0; line < 6; line++)
        {
            var length = component.Statistics.FormatLine(line, text, component.SceneLoadMilliseconds,
                TextureAsset.UploadedBytes);
            DrawLine(canvas, text.AsSpan(0, length), node.Rect.X + 8, node.Rect.Y + 17 + line * 18);
        }
    }

    void PrepareGlyphs(float size)
    {
        if (font.Size == size && glyphs[0] is not null) return;
        font.Size = size;
        Span<char> character = stackalloc char[1];
        for (var i = 0; i < glyphs.Length; i++)
        {
            glyphs[i]?.Dispose();
            character[0] = (char)(i + 32);
            glyphs[i] = SKTextBlob.Create(character, font);
            advances[i] = font.MeasureText(character);
        }
    }

    void DrawLine(SKCanvas canvas, ReadOnlySpan<char> line, float x, float y)
    {
        foreach (var character in line)
        {
            var index = character - 32;
            if ((uint)index >= glyphs.Length) continue;
            canvas.DrawText(glyphs[index], x, y, ink);
            x += advances[index];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        background.Dispose();
        ink.Dispose();
        font.Dispose();
        foreach (var glyph in glyphs) glyph?.Dispose();
    }
}
