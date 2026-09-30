namespace Turian.Engine.UI;

/// <summary>Thrown when a <c>.ui</c> document is malformed, carrying the source location.</summary>
public sealed class UiParseException(string message, int line, int column)
    : Exception($"{message} (line {line}, column {column})")
{
    /// <summary>1-based source line of the problem, or 0 if unknown.</summary>
    public int Line { get; } = line;

    /// <summary>1-based source column of the problem, or 0 if unknown.</summary>
    public int Column { get; } = column;
}
