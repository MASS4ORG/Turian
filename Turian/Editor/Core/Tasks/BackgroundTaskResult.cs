namespace Turian.Editor.Core;

/// <summary>The reported outcome of background work, including failures that need no exception.</summary>
/// <param name="Succeeded">Whether the operation completed successfully.</param>
/// <param name="Message">The result or an actionable explanation of the failure.</param>
public readonly record struct BackgroundTaskResult(bool Succeeded, string Message);
