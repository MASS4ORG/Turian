namespace Turian.Tests;

/// <summary>Tests for <see cref="UiValue"/> — inline-style scalar parsing.</summary>
public sealed class UiValueTests
{
    /// <summary>Length-like values parse with their unit and percentage flag.</summary>
    [Theory]
    [InlineData("12", 12f, false)]
    [InlineData("12px", 12f, false)]
    [InlineData("  40 px ", 40f, false)]
    [InlineData("50%", 0.5f, true)]
    [InlineData("100%", 1f, true)]
    public void TryLength(string text, float expected, bool expectPercent)
    {
        Assert.True(UiValue.TryLength(text, out var v, out var pct));
        Assert.Equal(expected, v, 3);
        Assert.Equal(expectPercent, pct);
    }

    /// <summary>Boolean-like values parse including numeric and off spellings.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("off", false)]
    public void TryBool(string text, bool expected)
    {
        Assert.True(UiValue.TryBool(text, out var v));
        Assert.Equal(expected, v);
    }

    /// <summary>A six-digit hex color parses with opaque alpha.</summary>
    [Fact]
    public void TryColor_Hex6()
    {
        Assert.True(UiValue.TryColor("#4a90e2", out var c));
        Assert.Equal(74, c.R);
        Assert.Equal(144, c.G);
        Assert.Equal(226, c.B);
        Assert.Equal(255, c.A);
    }

    /// <summary>An eight-digit hex color carries alpha.</summary>
    [Fact]
    public void TryColor_Hex8_WithAlpha()
    {
        Assert.True(UiValue.TryColor("#10203040", out var c));
        Assert.Equal(0x10, c.R);
        Assert.Equal(0x40, c.A);
    }

    /// <summary>A three-digit hex color expands to the six-digit form.</summary>
    [Fact]
    public void TryColor_Hex3_Expands()
    {
        Assert.True(UiValue.TryColor("#f00", out var c));
        Assert.Equal(255, c.R);
        Assert.Equal(0, c.G);
    }

    /// <summary>Functional rgb/rgba colors parse with integer or fractional alpha.</summary>
    [Theory]
    [InlineData("rgb(10, 20, 30)", 10, 20, 30, 255)]
    [InlineData("rgba(10, 20, 30, 0.5)", 10, 20, 30, 127)]
    [InlineData("rgba(10, 20, 30, 200)", 10, 20, 30, 200)]
    public void TryColor_Rgb(string text, int r, int g, int b, int a)
    {
        Assert.True(UiValue.TryColor(text, out var c));
        Assert.Equal(r, c.R);
        Assert.Equal(g, c.G);
        Assert.Equal(b, c.B);
        Assert.Equal(a, c.A, tolerance: 2);
    }

    /// <summary>Anything that is not a color is rejected.</summary>
    [Theory]
    [InlineData("not-a-color")]
    [InlineData("#xyz")]
    [InlineData("")]
    public void TryColor_Rejects(string text)
    {
        Assert.False(UiValue.TryColor(text, out _));
    }
}
