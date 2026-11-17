namespace Gaya.Plugin.Turian;

/// <summary>
/// Extracts logged source locations and opens their files with the operating system's associated application.
/// Missing source files are reported without launching an application.
/// </summary>
public static class LogSource
{
    static readonly Regex ParenForm = new(
        @"(?<path>.+)\((?<line>\d+)(?:,(?<col>\d+))?\)(?=:)", RegexOptions.Compiled);

    static readonly Regex ColonForm = new(
        @"(?<path>.+):(?<line>\d+)(?=:)", RegexOptions.Compiled);

    static readonly Regex StackForm = new(
        @"\bin (?<path>.+):line (?<line>\d+)", RegexOptions.Compiled);

    /// <summary>Uses an explicit message location or the source captured with the event.</summary>
    public static string LocationMessage(LogLine line) => TryParse(line.Text) is not null ? line.Text
        : line.SourceFile is not null && line.SourceLine > 0 ? $"{line.SourceFile}({line.SourceLine}):" : line.Text;

    /// <summary>The file and line a message references, when it references one.</summary>
    /// <param name="message">The rendered log message.</param>
    /// <returns>The file and line, or null when the message names no source location.</returns>
    public static (string Path, int Line)? TryParse(string message) =>
        Locate(message) is { } hit ? (hit.Path, hit.Line) : null;

    /// <summary>The file and line a message references, together with where that reference sits in the text.</summary>
    /// <param name="message">The rendered log message, or one line of it.</param>
    /// <returns>The reference, or null when the message names no source location.</returns>
    public static LogLocation? Locate(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        foreach (var match in new[] { ParenForm.Match(message), StackForm.Match(message), ColonForm.Match(message) })
        {
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line))
            {
                continue;
            }

            var group = match.Groups["path"];
            var path = group.Value.Trim();
            if (path.Length == 0)
            {
                continue;
            }

            var start = group.Index + group.Value.Length - group.Value.TrimStart().Length;
            return new LogLocation(path, line, start, match.Index + match.Length - start);
        }

        return null;
    }

    /// <summary>
    /// The clickable file references of a message: one per line that names an existing source file, so a stack
    /// trace's frames inside the project become links while engine and runtime frames stay plain text.
    /// </summary>
    /// <param name="text">The whole rendered message, one frame per line.</param>
    /// <param name="projectDirectory">The open project's folder, or null when none is open.</param>
    /// <returns>The links in text order, each pointing at its range of <paramref name="text"/>.</returns>
    public static IReadOnlyList<LogLink> Links(string text, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<LogLink>();
        var offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Locate(line) is { } hit && ResolvePath(line, projectDirectory) is not null)
            {
                links.Add(new LogLink(offset + hit.Start, hit.Length, line));
            }

            offset += raw.Length + 1;
        }

        return links;
    }

    /// <summary>
    /// The absolute path a message's source location refers to, or null when the file cannot be found.
    /// A rooted path is used as-is; a relative one is first resolved against the open project's directory
    /// (loggers usually emit <c>Assets/…</c> style references) and falls back to the process directory.
    /// </summary>
    /// <param name="message">The rendered log message.</param>
    /// <param name="projectDirectory">The open project's folder, or null when none is open.</param>
    /// <returns>The file path, or null when the message names no existing source file.</returns>
    public static string? ResolvePath(string message, string? projectDirectory)
    {
        var hit = TryParse(message);
        if (hit is not { } source)
        {
            return null;
        }

        if (Path.IsPathRooted(source.Path))
        {
            return File.Exists(source.Path) ? source.Path : null;
        }

        if (!string.IsNullOrEmpty(projectDirectory))
        {
            var inProject = Path.GetFullPath(Path.Combine(projectDirectory, source.Path));
            if (File.Exists(inProject))
            {
                return inProject;
            }
        }

        var inProcess = Path.GetFullPath(source.Path);
        return File.Exists(inProcess) ? inProcess : null;
    }

    /// <summary>
    /// Opens the source file with the operating system's associated application.
    /// Silently ignores a message with no source location.
    /// </summary>
    /// <param name="message">The rendered log message.</param>
    /// <param name="log">Where open failures are reported.</param>
    /// <param name="projectDirectory">The open project's folder, or null when none is open.</param>
    public static void Open(string message, ILogger log, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        var path = ResolvePath(message, projectDirectory);
        if (path is null)
        {
            ReportMissing(message, log);
            return;
        }

        LaunchSource(path, log, Process.Start);
    }

    static void ReportMissing(string message, ILogger log)
    {
        if (TryParse(message) is { } source)
        {
            log.LogWarning("Output: {Path} is not a source file, cannot jump to line {Line}", source.Path, source.Line);
        }
    }

    internal static void LaunchSource(string path, ILogger log, Func<ProcessStartInfo, Process?> start) =>
        TryLaunch(new ProcessStartInfo(path) { UseShellExecute = true }, log, start);

    static void TryLaunch(ProcessStartInfo info, ILogger log, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Output: could not open {File}", info.FileName);
        }
    }
}

/// <summary>A source reference found in a message: the file and line, and the character range that names them.</summary>
/// <param name="Path">The referenced file as written in the message.</param>
/// <param name="Line">The referenced line number.</param>
/// <param name="Start">Offset of the reference's first character in the message.</param>
/// <param name="Length">Length of the reference in characters.</param>
public readonly record struct LogLocation(string Path, int Line, int Start, int Length);

/// <summary>A clickable file reference inside a message's text.</summary>
/// <param name="Start">Offset of the link's first character in the message.</param>
/// <param name="Length">Length of the link in characters.</param>
/// <param name="Line">The message line holding the reference; opening it jumps to that file.</param>
public readonly record struct LogLink(int Start, int Length, string Line);
