namespace Turian.Tests;

/// <summary>
/// Tests for the immediate-mode <see cref="Gizmos"/> drawing API.
/// </summary>
public class GizmosTests
{
    readonly Gizmos gizmos = new();

    /// <summary>Lines go to the world collection and count by default.</summary>
    [Fact]
    public void DrawLine_AppendsToWorldByDefault()
    {
        gizmos.DrawLine(Vector3.Zero, new Vector3(1f, 0f, 0f));

        Assert.Single(gizmos.WorldLines);
        Assert.Empty(gizmos.OverlayLines);
        Assert.Equal(1, gizmos.LineCount);
    }

    /// <summary>With depth test disabled, lines go to the overlay collection.</summary>
    [Fact]
    public void DrawLine_WhenDepthTestDisabled_AppendsToOverlay()
    {
        gizmos.DepthTest = false;
        gizmos.DrawLine(Vector3.Zero, new Vector3(1f, 0f, 0f));

        Assert.Empty(gizmos.WorldLines);
        Assert.Single(gizmos.OverlayLines);
    }

    /// <summary>Drawn lines carry the current color and thickness.</summary>
    [Fact]
    public void DrawLine_CarriesColorAndThickness()
    {
        gizmos.Color = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        gizmos.Thickness = 4f;
        gizmos.DrawLine(Vector3.Zero, new Vector3(1f, 0f, 0f));

        var line = Assert.Single(gizmos.WorldLines);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), line.Color);
        Assert.Equal(4f, line.Thickness);
    }

    /// <summary>A ray spans from the origin along the given direction.</summary>
    [Fact]
    public void DrawRay_AppendsLineFromOriginAlongDirection()
    {
        gizmos.DrawRay(Vector3.Zero, new Vector3(2f, 3f, 4f));

        var line = Assert.Single(gizmos.WorldLines);
        Assert.Equal(Vector3.Zero, line.A);
        Assert.Equal(new Vector3(2f, 3f, 4f), line.B);
    }

    /// <summary>A wire cube is made of twelve edges.</summary>
    [Fact]
    public void DrawWireCube_ProducesTwelveEdges()
    {
        gizmos.DrawWireCube(Vector3.Zero, new Vector3(1f));

        Assert.Equal(12, gizmos.LineCount);
    }

    /// <summary>A wire sphere is drawn as three full circles.</summary>
    [Fact]
    public void DrawWireSphere_ProducesThreeCircles()
    {
        gizmos.DrawWireSphere(Vector3.Zero, 1.5f);

        Assert.Equal(3 * Gizmos.CircleSegments, gizmos.LineCount);
    }

    /// <summary>A circle produces one full segment count.</summary>
    [Fact]
    public void DrawCircle_ProducesCircleSegments()
    {
        gizmos.DrawCircle(Vector3.Zero, Vector3.UnitY, 1f);

        Assert.Equal(Gizmos.CircleSegments, gizmos.LineCount);
    }

    /// <summary>An arc of less than a full revolution still draws the full segment resolution.</summary>
    [Fact]
    public void DrawArc_DrawsSegmentCountMatchingSweep()
    {
        gizmos.DrawArc(Vector3.Zero, Vector3.UnitY, Vector3.UnitX, 90f, 1f);

        Assert.Equal(Gizmos.CircleSegments, gizmos.LineCount);
    }

    /// <summary>The gizmo matrix transforms both endpoints of a drawn line.</summary>
    [Fact]
    public void Matrix_TransformsBothEndpoints()
    {
        gizmos.Matrix = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        gizmos.DrawLine(Vector3.Zero, new Vector3(1f, 0f, 0f));

        var line = Assert.Single(gizmos.WorldLines);
        Assert.Equal(new Vector3(1f, 2f, 3f), line.A);
        Assert.Equal(new Vector3(2f, 2f, 3f), line.B);
    }

    /// <summary>Clearing resets both collections, the count and the overflow flag.</summary>
    [Fact]
    public void Clear_ResetsLinesAndOverflow()
    {
        gizmos.Color = new Vector4(1f, 0f, 0f, 1f);
        gizmos.DrawLine(Vector3.Zero, Vector3.One);
        gizmos.DepthTest = false;
        gizmos.DrawLine(Vector3.Zero, new Vector3(1f, 0f, 0f));

        gizmos.Clear();

        Assert.Empty(gizmos.WorldLines);
        Assert.Empty(gizmos.OverlayLines);
        Assert.Equal(0, gizmos.LineCount);
        Assert.False(gizmos.IsOverflow);
    }

    /// <summary>Overdrawing beyond the cap trims the count and sets the overflow flag.</summary>
    [Fact]
    public void Overflow_WhenExceedingMaxLines_CapsAndFlags()
    {
        for (var i = 0; i < Gizmos.MaxLines + 16; i++)
        {
            gizmos.DrawLine(Vector3.Zero, new Vector3(i, 0f, 0f));
        }

        Assert.True(gizmos.IsOverflow);
        Assert.Equal(Gizmos.MaxLines, gizmos.LineCount);
        Assert.Equal(Gizmos.MaxLines, gizmos.WorldLines.Count);
    }
}
