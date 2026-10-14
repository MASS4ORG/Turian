namespace Turian.Tests;

/// <summary>Tests for <see cref="UiStyleApplier"/> — inline <c>style</c> → <see cref="LayoutNode"/>.</summary>
public sealed class UiStyleApplierTests
{
    static LayoutNode BuildNode(Dictionary<string, string> style)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas);
        var node = gui.Node();
        UiStyleApplier.Apply(node, style);
        return node;
    }

    /// <summary>Direction, gap and grow map onto the layout style.</summary>
    [Fact]
    public void Flexbox_Basics()
    {
        var node = BuildNode(new Dictionary<string, string>
        {
            ["flex-direction"] = "row",
            ["gap"] = "14",
            ["flex-grow"] = "1",
        });

        Assert.Equal(Axis.Horizontal, node.Style.Direction);
        Assert.Equal(14f, node.Style.Gap);
        Assert.True(node.Style.IsExpanded);
    }

    /// <summary>Pixel and percentage sizes map to the right fields.</summary>
    [Fact]
    public void Sizing()
    {
        var node = BuildNode(new Dictionary<string, string>
        {
            ["width"] = "180px",
            ["height"] = "50%",
            ["max-width"] = "320",
            ["min-height"] = "24",
        });

        Assert.Equal(180f, node.Style.Width);
        Assert.Equal(0.5f, node.Style.HeightPercent, 3);
        Assert.Equal(320f, node.Style.MaxWidth);
        Assert.Equal(24f, node.Style.MinHeight);
    }

    /// <summary>Box shorthands: one value = all sides; two values = vertical horizontal.</summary>
    [Fact]
    public void Padding_Shorthands()
    {
        var one = BuildNode(new Dictionary<string, string> { ["padding"] = "10" });
        Assert.Equal(10f, one.Style.PaddingTop);
        Assert.Equal(10f, one.Style.PaddingLeft);

        var two = BuildNode(new Dictionary<string, string> { ["padding"] = "8 20" });
        Assert.Equal(8f, two.Style.PaddingTop);
        Assert.Equal(8f, two.Style.PaddingBottom);
        Assert.Equal(20f, two.Style.PaddingLeft);
        Assert.Equal(20f, two.Style.PaddingRight);
    }

    /// <summary>Alignment keywords map to fractions.</summary>
    [Fact]
    public void Alignment_Keywords()
    {
        var node = BuildNode(new Dictionary<string, string>
        {
            ["align-self"] = "flex-end",
            ["align-items"] = "center",
        });

        Assert.Equal(1f, node.Style.AlignSelf, 3);
        Assert.Equal(0.5f, node.Style.AlignContentHorizontal, 3);
    }

    /// <summary>Unknown declarations are ignored, not fatal.</summary>
    [Fact]
    public void UnknownDeclarations_AreIgnored()
    {
        var ex = Record.Exception(static () => BuildNode(new Dictionary<string, string>
        {
            ["background-color"] = "#123456",
            ["transition"] = "all 0.2s",
            ["gap"] = "6",
        }));

        Assert.Null(ex);
    }

    /// <summary>Alignment aliases configure all three layout axes consistently.</summary>
    [Theory]
    [InlineData("flex-start", 0f)]
    [InlineData("start", 0f)]
    [InlineData("left", 0f)]
    [InlineData("top", 0f)]
    [InlineData("center", 0.5f)]
    [InlineData("middle", 0.5f)]
    [InlineData("flex-end", 1f)]
    [InlineData("end", 1f)]
    [InlineData("right", 1f)]
    [InlineData("bottom", 1f)]
    [InlineData("unknown", 0f)]
    public void AlignmentAliases(string value, float expected)
    {
        var node = BuildNode(new Dictionary<string, string>
        {
            ["align-self"] = value,
            ["align-items"] = value,
            ["justify-content"] = value,
        });
        Assert.Equal(expected, node.Style.AlignSelf);
        Assert.Equal(expected, node.Style.AlignContentHorizontal);
        Assert.Equal(expected, node.Style.AlignContentVertical);
    }

    /// <summary>Four box values use CSS order: top, right, bottom and left.</summary>
    [Theory]
    [InlineData("padding")]
    [InlineData("margin")]
    public void FourSidedBoxes(string property)
    {
        var node = BuildNode(new Dictionary<string, string> { [property] = "1px 2px 3px 4px" });
        if (property == "padding")
        {
            Assert.Equal(1f, node.Style.PaddingTop);
            Assert.Equal(3f, node.Style.PaddingBottom);
            Assert.Equal(4f, node.Style.PaddingLeft);
            Assert.Equal(2f, node.Style.PaddingRight);
            return;
        }

        Assert.Equal(1f, node.Style.MarginTop);
        Assert.Equal(3f, node.Style.MarginBottom);
        Assert.Equal(4f, node.Style.MarginLeft);
        Assert.Equal(2f, node.Style.MarginRight);
    }

    /// <summary>Invalid numbers and unsupported shorthands leave existing layout values intact.</summary>
    [Theory]
    [InlineData("bad")]
    [InlineData("1 2 3")]
    [InlineData("1 2 3 4 5")]
    [InlineData("")]
    public void InvalidValuesPreserveLayout(string box)
    {
        var node = BuildNode(new Dictionary<string, string> { ["gap"] = "6", ["width"] = "20" });
        node.Padding(9);
        UiStyleApplier.Apply(node, new Dictionary<string, string>
        {
            ["gap"] = "bad",
            ["width"] = "auto",
            ["min-width"] = "bad",
            ["flex-grow"] = "0",
            ["padding"] = box,
        });
        Assert.Equal(6f, node.Style.Gap);
        Assert.Equal(20f, node.Style.Width);
        Assert.False(node.Style.IsExpanded);
        Assert.Equal(9f, node.Style.PaddingTop);
    }

    /// <summary>All size constraints and reversed flex directions are recognised.</summary>
    [Fact]
    public void ConstraintsAndPercentWidth()
    {
        var node = BuildNode(new Dictionary<string, string>
        {
            ["flex-direction"] = "row-reverse",
            ["width"] = "25%",
            ["height"] = "40px",
            ["min-width"] = "10px",
            ["max-width"] = "100px",
            ["min-height"] = "20px",
            ["max-height"] = "80px",
            ["margin"] = "5",
        });
        Assert.Equal(Axis.Horizontal, node.Style.Direction);
        Assert.Equal(0.25f, node.Style.WidthPercent);
        Assert.Equal(40f, node.Style.Height);
        Assert.Equal(10f, node.Style.MinWidth);
        Assert.Equal(100f, node.Style.MaxWidth);
        Assert.Equal(20f, node.Style.MinHeight);
        Assert.Equal(80f, node.Style.MaxHeight);
        Assert.Equal(5f, node.Style.MarginLeft);
    }
}
