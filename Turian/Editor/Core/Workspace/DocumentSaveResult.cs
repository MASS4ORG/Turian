namespace Turian.Editor.Core;

/// <summary>The outcome of saving a document.</summary>
/// <param name="Error">Why the source was not written, or null on success.</param>
public sealed record DocumentSaveResult(string? Error)
{
    /// <summary>The source holds the document's content.</summary>
    public static DocumentSaveResult Saved { get; } = new(Error: null);

    /// <summary>Whether the source holds the document's content.</summary>
    public bool Succeeded => Error is null;

    /// <summary>A save that wrote nothing and left the document dirty.</summary>
    /// <param name="error">Why, in one sentence.</param>
    /// <returns>The failed result.</returns>
    public static DocumentSaveResult Failed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new(error);
    }
}
