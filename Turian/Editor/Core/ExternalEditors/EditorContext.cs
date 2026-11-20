namespace Turian.Editor.Core;

/// <summary>What Studio asks an external editor to open: the project and, optionally, a place in one of its files.</summary>
/// <param name="ProjectDirectory">The project's folder; always present, so the editor can resolve references.</param>
/// <param name="Solution">The generated solution or project file to open, or <c>null</c> when there is none.</param>
/// <param name="File">The file to open, or <c>null</c> to open only the project.</param>
/// <param name="Line">The 1-based line to place the caret on; the first line when <c>null</c>.</param>
/// <param name="Column">The 1-based column to place the caret on; the first column when <c>null</c>.</param>
public sealed record EditorContext(
    string ProjectDirectory,
    string? Solution = null,
    string? File = null,
    int? Line = null,
    int? Column = null)
{
    /// <summary>
    /// What an editor is asked to open for the project: the generated solution for a solution-aware editor that has
    /// one, otherwise the project folder. A folder-based editor never receives the solution, so it cannot mistake the
    /// solution file for a file to add to a window of another project.
    /// </summary>
    /// <param name="solutionAware">Whether the editor opens solutions as projects, as Rider and Visual Studio do.</param>
    /// <returns>The solution path or the project folder.</returns>
    public string Target(bool solutionAware) => solutionAware && Solution is not null ? Solution : ProjectDirectory;

    /// <summary>The line to go to, 1 when none was given.</summary>
    public int EffectiveLine => Math.Max(1, Line ?? 1);

    /// <summary>The column to go to, 1 when none was given.</summary>
    public int EffectiveColumn => Math.Max(1, Column ?? 1);
}
