namespace Turian.Tests;

/// <summary>
/// Covers the Output detail pane's text layout: greedy word wrap with correct offsets into the
/// original message, the column mapping a pointer click resolves to, and the per-line highlight runs
/// a selection cuts. All pure measuring — no GUI types involved — so they run headless.
/// </summary>
public class WrappedTextLayoutTests
{
    static readonly SKFont font = new(SKTypeface.Default);

    /// <summary>A short line wraps to itself with its message offset.</summary>
    [Fact]
    public void WrapKeepsAShortLineWhole()
    {
        var lines = WrappedTextLayout.Wrap("short line", font, 1000f);

        var line = Assert.Single(lines);
        Assert.Equal("short line", line.Text);
        Assert.Equal(0, line.Start);
    }

    /// <summary>A line too wide breaks at the last space that fits.</summary>
    [Fact]
    public void WrapBreaksAtTheWordBoundary()
    {
        var lines = WrappedTextLayout.Wrap("first second", font, font.MeasureText("first"));

        Assert.Equal(2, lines.Count);
        Assert.Equal("first", lines[0].Text);
        Assert.Equal("second", lines[1].Text);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(6, lines[1].Start);
    }

    /// <summary>Paragraphs split on newlines, and each line keeps its offset into the raw message.</summary>
    [Fact]
    public void WrapSplitsParagraphsAndKeepsOffsets()
    {
        var lines = WrappedTextLayout.Wrap("first line\nsecond", font, 1000f);

        Assert.Equal(2, lines.Count);
        Assert.Equal("first line", lines[0].Text);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal("second", lines[1].Text);
        Assert.Equal("first line".Length + 1, lines[1].Start);
    }

    /// <summary>An empty paragraph is an empty line, keeping the message's blank lines visible.</summary>
    [Fact]
    public void WrapPreservesEmptyParagraphs()
    {
        var lines = WrappedTextLayout.Wrap("a\n\nb", font, 1000f);

        Assert.Equal(3, lines.Count);
        Assert.Equal("a", lines[0].Text);
        Assert.Equal("", lines[1].Text);
        Assert.Equal("b", lines[2].Text);
        Assert.Equal(2, lines[1].Start);
        Assert.Equal(3, lines[2].Start);
    }

    /// <summary>Runs of spaces survive the wrap, because a log's indentation matters.</summary>
    [Fact]
    public void WrapPreservesConsecutiveSpaces()
    {
        var line = Assert.Single(WrappedTextLayout.Wrap("a  b", font, 1000f));

        Assert.Equal("a  b", line.Text);
    }

    /// <summary>A single word wider than the pane still gets a line of its own.</summary>
    [Fact]
    public void WrapGivesAnOverwideWordItsOwnLine()
    {
        var wide = new string('x', 200);
        var lines = WrappedTextLayout.Wrap($"{wide} tail", font, 100f);

        Assert.Equal(wide, lines[0].Text);
        Assert.Equal("tail", lines[1].Text);
        Assert.Equal(wide.Length + 1, lines[1].Start);
    }

    /// <summary>A non-positive width skips wrapping entirely and still splits paragraphs.</summary>
    [Fact]
    public void WrapWithNoWidthJustSplitsNewlines() =>
        Assert.Equal(2, WrappedTextLayout.Wrap("a\nb", font, 0f).Count);

    /// <summary>An empty line maps any x to its single offset.</summary>
    [Fact]
    public void PositionInLineAnEmptyLineIsZero() =>
        Assert.Equal(0, WrappedTextLayout.PositionInLine(font, "", 100f));

    /// <summary>A click at the line's left edge is the first character.</summary>
    [Fact]
    public void PositionInLineAtTheLeftEdgeIsZero() =>
        Assert.Equal(0, WrappedTextLayout.PositionInLine(font, "first", 0f));

    /// <summary>A click at a character boundary resolves to that column.</summary>
    [Fact]
    public void PositionInLineResolvesToTheNearestBoundary() =>
        Assert.Equal(2, WrappedTextLayout.PositionInLine(font, "first", font.MeasureText("fi")));

    /// <summary>A click past the end of the line is the last character.</summary>
    [Fact]
    public void PositionInLinePastTheEndIsTheLength() =>
        Assert.Equal("first".Length, WrappedTextLayout.PositionInLine(font, "first", 10_000f));

    /// <summary>A run fully inside a line yields that line's character span and pixel run.</summary>
    [Fact]
    public void SelectionOnAnEnclosedRun()
    {
        var line = new WrappedLine("first", 0);
        var selection = WrappedTextLayout.SelectionOn(font, line, selectionStart: 1, selectionEnd: 3);

        Assert.NotNull(selection);
        Assert.Equal(1, selection.Value.Start);
        Assert.Equal(3, selection.Value.End);
        Assert.Equal(font.MeasureText("f"), selection.Value.X);
        Assert.Equal(font.MeasureText("fir") - font.MeasureText("f"), selection.Value.Width, precision: 1);
    }

    /// <summary>A run whose bounds fall outside the line touches nothing.</summary>
    [Fact]
    public void SelectionOnNothingWhenOutsideTheLine()
    {
        var line = new WrappedLine("first", 0);

        Assert.Null(WrappedTextLayout.SelectionOn(font, line, 5, 7));
        Assert.Null(WrappedTextLayout.SelectionOn(font, line, -2, 0));
    }

    /// <summary>A selection crossing the line's start or end is clamped to the line.</summary>
    [Fact]
    public void SelectionOnClampsToTheLine()
    {
        var line = new WrappedLine("first", 6);
        var selection = WrappedTextLayout.SelectionOn(font, line, selectionStart: 2, selectionEnd: 20);

        Assert.NotNull(selection);
        Assert.Equal(6, selection.Value.Start);
        Assert.Equal(11, selection.Value.End);
    }

    /// <summary>A line untouched by the selection — a wrapped neighbour — yields nothing.</summary>
    [Fact]
    public void SelectionOnAGapLineYieldsNothing()
    {
        var line = new WrappedLine("second", 6);

        Assert.Null(WrappedTextLayout.SelectionOn(font, line, selectionStart: 0, selectionEnd: 5));
    }
}
