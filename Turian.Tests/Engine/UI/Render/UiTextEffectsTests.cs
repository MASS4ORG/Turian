namespace Turian.Tests;

/// <summary>Tests for <see cref="UiTextEffects"/> — parsing <c>.uss</c> text-effect declarations.</summary>
public sealed class UiTextEffectsTests
{
    static Func<string, string> Style(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) map[k] = v;
        return name => map.TryGetValue(name, out var v) ? v : string.Empty;
    }

    /// <summary>No text-effect declarations resolve to null.</summary>
    [Fact]
    public void NoDeclarations_ReturnsNull() =>
        Assert.Null(UiTextEffects.Resolve(Style()));

    /// <summary>An outline declaration parses its width and color.</summary>
    [Fact]
    public void Outline_IsParsed()
    {
        var fx = UiTextEffects.Resolve(Style(("text-outline", "#101216 3")));

        Assert.NotNull(fx!.Value.Outline);
        Assert.Equal(3f, fx.Value.Outline!.Value.Width);
        Assert.Equal(0x10, fx.Value.Outline.Value.Color.R);
    }

    /// <summary>A drop shadow parses its offset and blur.</summary>
    [Fact]
    public void Shadow_ParsesOffsetAndBlur()
    {
        var fx = UiTextEffects.Resolve(Style(("text-shadow", "#000000aa 2 4 6")));

        Assert.NotNull(fx!.Value.DropShadow);
        Assert.Equal(new Vector2(2, 4), fx.Value.DropShadow!.Value.Offset);
        Assert.Equal(6f, fx.Value.DropShadow.Value.Blur);
    }

    /// <summary>A shadow without a blur value defaults it to zero.</summary>
    [Fact]
    public void Shadow_BlurDefaultsToZero()
    {
        var fx = UiTextEffects.Resolve(Style(("text-inner-shadow", "#00000088 0 2")));

        Assert.NotNull(fx!.Value.InnerShadow);
        Assert.Equal(0f, fx.Value.InnerShadow!.Value.Blur);
    }

    /// <summary>A linear gradient parses its angle and is not flagged radial.</summary>
    [Fact]
    public void Gradient_LinearWithAngle_IsParsed()
    {
        var fx = UiTextEffects.Resolve(Style(("text-gradient", "linear(#8ab4f8, #f28b82, 25)")));

        Assert.NotNull(fx!.Value.Gradient);
        Assert.False(fx.Value.Gradient!.Value.Radial);
        Assert.Equal(25f, fx.Value.Gradient.Value.AngleDegrees);
    }

    /// <summary>A radial gradient is flagged as radial.</summary>
    [Fact]
    public void Gradient_Radial_IsFlagged()
    {
        var fx = UiTextEffects.Resolve(Style(("text-gradient", "radial(#000000, #ffffff)")));

        Assert.True(fx!.Value.Gradient!.Value.Radial);
    }

    /// <summary>All four declarations are returned together.</summary>
    [Fact]
    public void Combined_ReturnsAllFour()
    {
        var fx = UiTextEffects.Resolve(Style(
            ("text-outline", "#000000 2"),
            ("text-shadow", "#00000088 0 3 6"),
            ("text-inner-shadow", "#000000aa 0 1 2"),
            ("text-gradient", "linear(#111111, #eeeeee)")));

        Assert.NotNull(fx!.Value.Outline);
        Assert.NotNull(fx.Value.DropShadow);
        Assert.NotNull(fx.Value.InnerShadow);
        Assert.NotNull(fx.Value.Gradient);
    }

    /// <summary>Malformed declarations are ignored rather than throwing.</summary>
    [Fact]
    public void Malformed_IsIgnored()
    {
        var fx = UiTextEffects.Resolve(Style(
            ("text-outline", "not-a-color"),
            ("text-gradient", "linear(#fff)")));

        Assert.Null(fx);
    }
}
