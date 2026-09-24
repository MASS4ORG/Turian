namespace Turian.Editor.Core;

/// <summary>
/// Represents a named background build task that can optionally lock the UI.
/// </summary>
public interface IBuildTask
{
    /// <summary>Human-readable task name shown in progress UI.</summary>
    string Name { get; }

    /// <summary>
    /// When <c>true</c> the UI should be considered locked/disabled while this task runs.
    /// </summary>
    bool LocksUi { get; }

    /// <summary>Executes the task and returns an optional result message.</summary>
    Task<string> RunAsync(CancellationToken cancellationToken);
}
