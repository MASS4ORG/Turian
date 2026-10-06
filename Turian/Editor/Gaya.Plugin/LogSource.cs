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
    public static (string Path, int Line)? TryParse(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        foreach (var match in new[] { ParenForm.Match(message), StackForm.Match(message), ColonForm.Match(message) })
        {
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line)) continue;

            var path = match.Groups["path"].Value.Trim();
            if (path.Length == 0 || string.IsNullOrWhiteSpace(path)) continue;

            return (path, line);
        }

        return null;
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
        if (hit is not { } source) return null;

        if (Path.IsPathRooted(source.Path)) return File.Exists(source.Path) ? source.Path : null;

        if (!string.IsNullOrEmpty(projectDirectory))
        {
            var inProject = Path.GetFullPath(Path.Combine(projectDirectory, source.Path));
            if (File.Exists(inProject)) return inProject;
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
            log.LogWarning("Output: {Path} is not a source file, cannot jump to line {Line}", source.Path, source.Line);
    }

    internal static void LaunchSource(string path, ILogger log, Func<ProcessStartInfo, Process?> start) =>
        TryLaunch(new ProcessStartInfo(path) { UseShellExecute = true }, log, start);

    static bool TryLaunch(ProcessStartInfo info, ILogger log, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            start(info)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Output: could not open {File}", info.FileName);
            return false;
        }
    }

}
