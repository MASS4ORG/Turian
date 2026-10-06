namespace Gaya.Plugin.Turian;

/// <summary>One rendered log line.</summary>
/// <param name="Level">The event's log level.</param>
/// <param name="Timestamp">When the event happened.</param>
/// <param name="Text">The rendered message.</param>
public readonly record struct LogLine(LogLevel Level, DateTimeOffset Timestamp, string Text)
{
    /// <summary>The logger category that produced the event.</summary>
    public string Category { get; init; } = "";

    /// <summary>Whether the event came from Studio or its engine services.</summary>
    public bool IsInternal { get; init; }

    /// <summary>The caller's source file, when debug symbols provide it.</summary>
    public string? SourceFile { get; init; }

    /// <summary>The caller's source line, or zero when unavailable.</summary>
    public int SourceLine { get; init; }
}
