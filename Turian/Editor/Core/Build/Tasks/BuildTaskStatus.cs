namespace Turian.Editor.Core;

/// <summary>State of a background build task.</summary>
public enum BuildTaskState
{
    /// <summary>The task is currently running.</summary>
    Running,
    /// <summary>The task completed successfully.</summary>
    Succeeded,
    /// <summary>The task failed with an error.</summary>
    Failed,
    /// <summary>The task was cancelled.</summary>
    Cancelled
}

/// <summary>Snapshot of a task's execution result.</summary>
/// <param name="TaskName">The task that was executed.</param>
/// <param name="State">Final state.</param>
/// <param name="Message">Human-readable result or error message.</param>
/// <param name="LockedUi">Whether the task had UI locking enabled.</param>
public sealed record BuildTaskStatus(
    string TaskName,
    BuildTaskState State,
    string Message,
    bool LockedUi);
