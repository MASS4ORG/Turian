namespace Turian.Tests;

/// <summary>Tests for <see cref="BindingExpression"/> parsing.</summary>
public sealed class BindingExpressionTests
{
    /// <summary>A bare path binds one-way with no converter.</summary>
    [Fact]
    public void Path_Only()
    {
        var b = BindingExpression.Parse("{Player.Health}");

        Assert.Equal("Player.Health", b.Path);
        Assert.Equal(BindingMode.OneWay, b.Mode);
        Assert.Null(b.Converter);
    }

    /// <summary>An explicit mode is parsed case-insensitively.</summary>
    [Theory]
    [InlineData("{Volume, mode=TwoWay}", BindingMode.TwoWay)]
    [InlineData("{X, mode=onetime}", BindingMode.OneTime)]
    public void Mode_IsParsed(string text, BindingMode expected)
    {
        Assert.Equal(expected, BindingExpression.Parse(text).Mode);
    }

    /// <summary>A trailing <c>| name</c> is the converter.</summary>
    [Fact]
    public void Converter_IsParsed()
    {
        var b = BindingExpression.Parse("{Score | thousands}");

        Assert.Equal("Score", b.Path);
        Assert.Equal("thousands", b.Converter);
    }

    /// <summary>Mode and converter together.</summary>
    [Fact]
    public void Mode_And_Converter()
    {
        var b = BindingExpression.Parse("{Enabled, mode=OneTime | not}");

        Assert.Equal("Enabled", b.Path);
        Assert.Equal(BindingMode.OneTime, b.Mode);
        Assert.Equal("not", b.Converter);
    }

    /// <summary><see cref="BindingExpression.IsBinding"/> only accepts brace-wrapped text.</summary>
    [Theory]
    [InlineData("{a}", true)]
    [InlineData("plain", false)]
    [InlineData("{unterminated", false)]
    [InlineData("", false)]
    public void IsBinding(string text, bool expected)
    {
        Assert.Equal(expected, BindingExpression.IsBinding(text));
    }

    /// <summary>Non-expressions and empty paths are rejected.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("{}")]
    [InlineData("{ | conv}")]
    public void Parse_Rejects_Malformed(string text)
    {
        Assert.Throws<FormatException>(() => BindingExpression.Parse(text));
    }
}
