namespace Gaya.Plugin.Turian;

/// <summary>
/// Build-time metadata: the version this build was compiled at, the compilation date, and the
/// contributor list from <c>docs/CONTRIBUTORS.md</c>. The fields below are overwritten by the
/// generated half of this partial class (<c>BuildInfoGenerator</c>); the values here are only used
/// if that generator did not run.
/// </summary>
public static partial class BuildInfo
{
    /// <summary>The version this build was compiled at, from <c>git describe</c>.</summary>
    public static string Version => SVersion;

    /// <summary>The contributors listed in <c>docs/CONTRIBUTORS.md</c>, one per line.</summary>
    public static string Contributors => SContributors;

    /// <summary>The UTC date this assembly was compiled, formatted <c>yyyy-MM-dd</c>.</summary>
    public static string CompilationDate => SCompilationDate;

    internal static string SVersion = "dev";
    internal static string SContributors = "(contributors list not available)";
    internal static string SCompilationDate = "(unknown)";
}
