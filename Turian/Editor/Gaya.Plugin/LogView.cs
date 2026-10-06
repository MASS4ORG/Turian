namespace Gaya.Plugin.Turian;

/// <summary>
/// The projection of the raw log buffer into what the Output panel draws: the message filter, the
/// severity toggles and the collapse pass that merges consecutive duplicates into one row with a
/// count. Kept side-effect free so the panel stays a thin shell over the rules, and the rules stay
/// testable.
/// </summary>
public static class LogView
{
    /// <summary>
    /// Whether a level survives the Output panel's severity toggles.
    /// </summary>
    /// <param name="level">The event's log level.</param>
    /// <param name="errors">Whether the Errors toggle is on; gates <see cref="LogLevel.Critical"/> and <see cref="LogLevel.Error"/>.</param>
    /// <param name="warnings">Whether the Warnings toggle is on; gates <see cref="LogLevel.Warning"/>.</param>
    /// <param name="log">Whether Debug and Trace are visible.</param>
    /// <param name="information">Whether Information is visible; null uses the Debug toggle.</param>
    /// <returns>True when the level is visible under the given toggles.</returns>
    public static bool Visible(LogLevel level, bool errors, bool warnings, bool log, bool? information = null) => level switch
    {
        LogLevel.Critical or LogLevel.Error => errors,
        LogLevel.Warning => warnings,
        LogLevel.Information => information ?? log,
        LogLevel.Debug or LogLevel.Trace => log,
        _ => false,
    };

    /// <summary>
    /// Turns the raw buffer into the rows the panel draws: the text filter and the severity toggles
    /// decide what stays, and when <paramref name="collapse"/> is set, consecutive duplicate messages
    /// merge into one row carrying the number of iterations it stands for. Collapsing operates on the
    /// already-filtered stream, so a toggle that hides a message does not break its neighbour's run.
    /// </summary>
    /// <param name="lines">The raw buffered lines, oldest first.</param>
    /// <param name="filter">The message substring filter; empty keeps everything.</param>
    /// <param name="errors">Whether error rows are visible.</param>
    /// <param name="warnings">Whether warning rows are visible.</param>
    /// <param name="log">Whether the non-error/warning rows are visible.</param>
    /// <param name="collapse">Whether consecutive duplicates collapse into one row with a count.</param>
    /// <param name="information">Whether Information rows are visible; null uses the Debug toggle.</param>
    /// <param name="studio">Whether events from Studio and engine services are visible.</param>
    /// <returns>The visible rows, oldest first.</returns>
    public static IReadOnlyList<LogRow> Project(IReadOnlyList<LogLine> lines, string filter,
        bool errors, bool warnings, bool log, bool collapse, bool? information = null, bool studio = true)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<LogRow>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!Matches(line, filter, errors, warnings, log, information, studio)) continue;

            if (!collapse || rows.Count == 0 || !SameEvent(rows[^1].Line, line))
            {
                rows.Add(new LogRow(line, 1, index));
            }
            else
            {
                rows[^1] = rows[^1] with { Count = rows[^1].Count + 1 };
            }
        }

        return rows;
    }

    static bool Matches(LogLine line, string filter, bool errors, bool warnings, bool debug,
        bool? information, bool studio) => (studio || !line.IsInternal)
        && Visible(line.Level, errors, warnings, debug, information)
        && (filter.Length == 0 || line.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));

    static bool SameEvent(LogLine first, LogLine second) => first.Level == second.Level
        && first.Text == second.Text && first.Category == second.Category
        && first.SourceFile == second.SourceFile && first.SourceLine == second.SourceLine;

    /// <summary>
    /// Reduces a message to its first <paramref name="lineCount"/> lines, the cap the Entry Lines
    /// setting puts on a list entry — the full message stays reachable in the details pane. An
    /// ellipsis marks a message cut short.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="lineCount">The maximum number of lines to keep; one keeps a single line.</param>
    /// <returns>The message reduced to at most <paramref name="lineCount"/> lines.</returns>
    public static string FirstLines(string text, int lineCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (lineCount <= 1)
        {
            var line = text.Split('\n')[0];
            return line.Length < text.Length ? line + "…" : line;
        }

        var lines = text.Split('\n');
        if (lines.Length <= lineCount) return text;

        return string.Join('\n', lines.Take(lineCount)) + "…";
    }
}

/// <summary>One visible row of the Output panel.</summary>
/// <param name="Line">The representative buffered line.</param>
/// <param name="Count">What collapse merged into it; 1 means the message appeared once.</param>
/// <param name="RawIndex">The line's position in the raw buffer — the row's stable id while other lines scroll by.</param>
public readonly record struct LogRow(LogLine Line, int Count, int RawIndex);
