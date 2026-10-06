namespace Gaya.Plugin.Turian;

/// <summary>
/// Extracts the source file and line a log message points at — the <c>path(line,col):</c> form the
/// MSBuild logger emits and the <c>path:line:</c> form other tools use — so a double click in the
/// Output panel can hand them to an editor that jumps there. The lookup is best effort: a message
/// with no location does nothing, and a location whose file does not exist logs once instead of
/// launching a bogus process.
/// </summary>
public static class LogSource
{
    static readonly string[] LineJumperEditors = ["code", "codium", "cursor", "zed"];

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
    /// Opens the source a message points at, positioned on its line. Prefers an editor that accepts
    /// source coordinates when one is on PATH; otherwise the file goes to the desktop's default
    /// program. Silently ignores a message with no source location.
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

        LaunchSource(path, TryParse(message)!.Value.Line, log, OnPath, Process.Start);
    }

    static void ReportMissing(string message, ILogger log)
    {
        if (TryParse(message) is { } source)
            log.LogWarning("Output: {Path} is not a source file, cannot jump to line {Line}", source.Path, source.Line);
    }

    internal static void LaunchSource(string path, int line, ILogger log, Func<string, bool> onPath,
        Func<ProcessStartInfo, Process?> start)
    {
        var editor = LineJumperEditors.FirstOrDefault(onPath);
        if (editor is not null && TryLaunch(EditorStartInfo(editor, path, line), log, start)) return;
        TryLaunch(new ProcessStartInfo(path) { UseShellExecute = true }, log, start);
    }

    internal static ProcessStartInfo EditorStartInfo(string editor, string path, int line)
    {
        var info = new ProcessStartInfo(editor) { UseShellExecute = false };
        if (editor != "zed") info.ArgumentList.Add("--goto");
        info.ArgumentList.Add($"{path}:{line}");
        return info;
    }

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

    static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return false;

        return path.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, executable))
            .Any(candidate => File.Exists(candidate) || File.Exists(candidate + ".exe"));
    }
}
