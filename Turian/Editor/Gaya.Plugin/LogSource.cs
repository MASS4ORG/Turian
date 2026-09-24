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
    static readonly string[] lineJumperEditors = ["code", "codium", "cursor", "zed"];

    static readonly Regex parenForm = new(
        @"(?<path>.+)\((?<line>\d+)(?:,(?<col>\d+))?\)(?=:)", RegexOptions.Compiled);

    static readonly Regex colonForm = new(
        @"(?<path>.+):(?<line>\d+)(?=:)", RegexOptions.Compiled);

    /// <summary>The file and line a message references, when it references one.</summary>
    /// <param name="message">The rendered log message.</param>
    /// <returns>The file and line, or null when the message names no source location.</returns>
    public static (string Path, int Line)? TryParse(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        foreach (var match in new[] { parenForm.Match(message), colonForm.Match(message) })
        {
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line)) continue;

            var path = match.Groups["path"].Value;
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
    /// <c>--goto path:line</c> when one is on PATH; otherwise the file goes to the desktop's default
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
            var hit = TryParse(message);
            if (hit is { } source)
                log.LogWarning("Output: {Path} is not a source file, cannot jump to line {Line}",
                    source.Path, source.Line);
            return;
        }

        var sourceLine = TryParse(message)!.Value.Line;

        var editor = lineJumperEditors.FirstOrDefault(OnPath);
        if (editor is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(editor, $"--goto \"{path}\":{sourceLine}")
                {
                    UseShellExecute = false,
                })?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Output: {Editor} could not open {Path}:{Line}", editor, path, sourceLine);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Output: no editor opened {Path}", path);
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
