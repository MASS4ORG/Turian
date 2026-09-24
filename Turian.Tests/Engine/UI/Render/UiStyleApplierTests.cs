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
        var ex = Record.Exception(() => BuildNode(new Dictionary<string, string>
        {
            ["background-color"] = "#123456",
            ["transition"] = "all 0.2s",
            ["gap"] = "6",
        }));

        Assert.Null(ex);
    }
}
