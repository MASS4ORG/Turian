namespace Gaya.Plugin.Turian;

/// <summary>One rendered log line.</summary>
/// <param name="Level">The event's log level.</param>
/// <param name="Timestamp">When the event happened.</param>
/// <param name="Text">The rendered message.</param>
public readonly record struct LogLine(LogLevel Level, DateTimeOffset Timestamp, string Text);
