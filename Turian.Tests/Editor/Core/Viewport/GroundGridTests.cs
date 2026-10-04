namespace Turian.Tests;

/// <summary>Checks world-anchored grid intervals, configurable planes and drawing state.</summary>
public sealed class GroundGridTests
{
    /// <summary>Intermediate and major lines stay on world multiples when the camera changes cells.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(3.5f)]
    [InlineData(-7.5f)]
    public void GridHasOneFiveAndTenUnitLevelsAnchoredToOrigin(float cameraX)
    {
        var drawing = new Gizmos();
        GroundGrid.Draw(drawing, new Vector3(cameraX, 2, -5));
        Assert.Equal(202, drawing.WorldLines.Count);
        Assert.Empty(drawing.OverlayLines);
        foreach (var line in drawing.WorldLines)
        {
            Assert.Equal(0, line.A.Y);
            Assert.Equal(0, line.B.Y);
            var index = line.A.X == line.B.X ? line.A.X : line.A.Z;
            Assert.Equal(MathF.Round(index), index);
            var expected = index == 0 || index % 10 == 0 ? 2f : index % 5 == 0 ? 1.5f : 1f;
            Assert.Equal(expected, line.Thickness);
        }
    }

    /// <summary>All plane choices, colored axes and custom spacing honor settings and restore drawing state.</summary>
    [Theory]
    [InlineData(SceneGridPlane.Xy)]
    [InlineData(SceneGridPlane.Xz)]
    [InlineData(SceneGridPlane.Yz)]
    public void GridSettingsControlPlaneAxesAndSpacing(SceneGridPlane plane)
    {
        var settings = new SceneGridSettings
        {
            Plane = plane,
            CellSize = 2,
            HalfExtent = 10,
            ShowNormalAxis = true,
            IntermediateEvery = 3,
            MajorEvery = 6
        };
        var drawing = new Gizmos
        {
            Matrix = Matrix4x4.CreateTranslation(100, 100, 100),
            DepthTest = false,
            Color = Vector4.One,
            Thickness = 7
        };
        GroundGrid.Draw(drawing, Vector3.Zero, settings);
        Assert.Equal(43, drawing.WorldLines.Count);
        Assert.Empty(drawing.OverlayLines);
        Assert.Equal(Matrix4x4.CreateTranslation(100, 100, 100), drawing.Matrix);
        Assert.Equal(Vector4.One, drawing.Color);
        Assert.False(drawing.DepthTest);
        Assert.Equal(7f, drawing.Thickness);
        Assert.Equal(3, drawing.WorldLines.Count(line => line.Color.X != line.Color.Y));
        settings.ShowPlaneAxes = settings.ShowNormalAxis = false;
        drawing.Clear();
        GroundGrid.Draw(drawing, Vector3.Zero, settings);
        Assert.All(drawing.WorldLines, line => Assert.Equal(line.Color.X, line.Color.Y));
        settings.Visible = false;
        drawing.Clear();
        GroundGrid.Draw(drawing, Vector3.Zero, settings);
        Assert.Equal(0, drawing.LineCount);
    }
}
