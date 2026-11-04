namespace MASS4.Bricks;

/// <summary>
/// Git options that write files with the line endings the repository itself declares, ignoring the user's
/// <c>core.autocrlf</c> and <c>core.eol</c>, so a brick checked out on Windows has the same bytes and integrity as on
/// Linux or macOS.
/// </summary>
static class GitLineEndings
{
    /// <summary>The options, to place before the git command.</summary>
    public static readonly string[] Arguments = ["-c", "core.autocrlf=false", "-c", "core.eol=lf"];
}
