namespace Turian.Editor.Core;

/// <summary>
/// Controls the optimization and debug-symbol strategy for a build.
/// </summary>
public enum BuildConfiguration
{
    /// <summary>
    /// Full debug symbols, no optimizations.  Used for in-editor play mode.
    /// </summary>
    Debug,

    /// <summary>
    /// Optimized, no debug symbols, single-file self-contained output.  Used for final export.
    /// </summary>
    Release
}
