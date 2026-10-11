namespace Turian.Editor.Core;

/// <summary>The visible grid bands shared by both GUI passes.</summary>
public readonly record struct AssetGridWindow(int Columns, int FirstRow, int LastRow, int RowCount);

/// <summary>Calculates virtualized tile bands without constructing offscreen cells.</summary>
public static class AssetGridLayout
{
    /// <summary>Returns visible rows with one band of overscan on either side.</summary>
    public static AssetGridWindow Calculate(int count, float width, float cellWidth, float rowHeight,
        float scroll, float viewportHeight)
    {
        var columns = Math.Max(1, (int)(width / Math.Max(1, cellWidth)));
        var rows = (count + columns - 1) / columns;
        var first = Math.Clamp((int)(scroll / Math.Max(1, rowHeight)) - 1, 0, rows);
        var last = Math.Min(rows, first + (int)(viewportHeight / Math.Max(1, rowHeight)) + 3);
        return new AssetGridWindow(columns, first, last, rows);
    }
}
