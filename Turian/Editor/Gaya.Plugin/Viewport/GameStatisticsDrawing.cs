namespace Gaya.Plugin.Turian;

/// <summary>Draws the Game panel's opt-in metrics with reusable character storage and cached glyphs.</summary>
sealed class GameStatisticsDrawing(FrameStatistics statistics, SceneTreeController sceneTree) : IDrawable, IDisposable
{
    readonly char[] text = new char[192];
    readonly SKPaint background = new() { Color = new SKColor(20, 24, 30, 235) };
    readonly SKPaint ink = new() { IsAntialias = true, Color = SKColors.White };
    readonly SKFont font = new(SKTypeface.Default, 11);
    readonly SKTextBlob?[] glyphs = new SKTextBlob?[95];
    readonly float[] advances = new float[95];

    /// <inheritdoc />
    public SKPaint Paint => ink;

    /// <inheritdoc />
    public void Render(Gui gui, LayoutNode node, SKCanvas canvas)
    {
        PrepareGlyphs();
        var x = node.Rect.X + Math.Max(0, node.Rect.W - 412);
        var y = node.Rect.Y + 12;
        canvas.DrawRoundRect(new SKRect(x, y, x + 400, y + 122), 4, 4, background);
        for (var line = 0; line < 6; line++)
        {
            var length = statistics.FormatLine(line, text, sceneTree.CurrentSceneLoadMilliseconds,
                TextureAsset.UploadedBytes);
            DrawLine(canvas, text.AsSpan(0, length), x + 8, y + 17 + line * 18);
        }
    }

    void PrepareGlyphs()
    {
        if (glyphs[0] is not null) return;
        Span<char> character = stackalloc char[1];
        for (var i = 0; i < glyphs.Length; i++)
        {
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
