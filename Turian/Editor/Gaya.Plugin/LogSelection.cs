namespace Gaya.Plugin.Turian;

/// <summary>
/// The set of Output entries the user has selected, keyed by each row's raw buffer index so it survives filter,
/// collapse and tail changes. Click, Ctrl+click and Shift+click gestures map onto <see cref="Set"/>,
/// <see cref="Toggle"/> and <see cref="Extend"/>; <see cref="Text"/> is what Copy puts on the clipboard.
/// </summary>
public sealed class LogSelection
{
    readonly HashSet<int> selected = [];
    int anchor = -1;

    /// <summary>How many entries are selected.</summary>
    public int Count => selected.Count;

    /// <summary>Whether a row's entry is selected.</summary>
    /// <param name="row">The row to test.</param>
    /// <returns>True when the row is part of the selection.</returns>
    public bool Contains(LogRow row) => selected.Contains(row.RawIndex);

    /// <summary>Empties the selection.</summary>
    public void Clear()
    {
        selected.Clear();
        anchor = -1;
    }

    /// <summary>Selects only the row at <paramref name="index"/> and makes it the range anchor.</summary>
    /// <param name="rows">The visible rows.</param>
    /// <param name="index">The clicked row.</param>
    public void Set(IReadOnlyList<LogRow> rows, int index)
    {
        selected.Clear();
        Add(rows, index);
    }

    /// <summary>Flips one row in or out of the selection, keeping the others, and anchors on it.</summary>
    /// <param name="rows">The visible rows.</param>
    /// <param name="index">The Ctrl+clicked row.</param>
    public void Toggle(IReadOnlyList<LogRow> rows, int index)
    {
        if (!selected.Remove(rows[index].RawIndex))
        {
            _ = selected.Add(rows[index].RawIndex);
        }

        anchor = index;
    }

    /// <summary>Selects every row between the anchor and <paramref name="index"/>, inclusive.</summary>
    /// <param name="rows">The visible rows.</param>
    /// <param name="index">The Shift+clicked row.</param>
    /// <param name="additive">Whether the existing selection stays; otherwise the range replaces it.</param>
    public void Extend(IReadOnlyList<LogRow> rows, int index, bool additive)
    {
        var from = anchor < 0 ? index : Math.Min(anchor, rows.Count - 1);
        if (!additive)
        {
            selected.Clear();
        }

        for (var row = Math.Min(from, index); row <= Math.Max(from, index); row++)
        {
            _ = selected.Add(rows[row].RawIndex);
        }

        if (anchor < 0)
        {
            anchor = index;
        }
    }

    /// <summary>Selects every visible row.</summary>
    /// <param name="rows">The visible rows.</param>
    public void SelectAll(IReadOnlyList<LogRow> rows)
    {
        selected.Clear();
        foreach (var row in rows)
        {
            _ = selected.Add(row.RawIndex);
        }

        anchor = rows.Count > 0 ? 0 : -1;
    }

    /// <summary>The index of the last selected row, or -1 when none of the visible rows is selected.</summary>
    /// <param name="rows">The visible rows.</param>
    /// <returns>The row index.</returns>
    public int LastIndex(IReadOnlyList<LogRow> rows)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (selected.Contains(rows[index].RawIndex))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The full text of the selected entries, whole stack traces included, in list order.</summary>
    /// <param name="rows">The visible rows.</param>
    /// <returns>The entries separated by line breaks; empty when nothing visible is selected.</returns>
    public string Text(IReadOnlyList<LogRow> rows) => string.Join(Environment.NewLine,
        rows.Where(Contains).Select(static row => row.Line.Text));

    void Add(IReadOnlyList<LogRow> rows, int index)
    {
        _ = selected.Add(rows[index].RawIndex);
        anchor = index;
    }
}
