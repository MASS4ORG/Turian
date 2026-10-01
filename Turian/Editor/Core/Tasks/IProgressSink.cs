namespace Turian.Editor.Core;

/// <summary>
/// Progress and cancellation channel handed to a long-running editor operation. It lets the
/// operation describe its shape — nested phases, item counters — without referencing the registry
/// that renders it. <see cref="NullProgressSink.Instance"/> is the no-op default, so an operation
/// runs unobserved when nobody is watching.
/// </summary>
public interface IProgressSink
{
    /// <summary>True once cancellation has been requested. Poll at convenient checkpoints.</summary>
    bool IsCancelled { get; }

    /// <summary>Publish a completion fraction (clamped to 0..1) and an optional status note.</summary>
    /// <param name="fraction">Completion in 0..1.</param>
    /// <param name="note">Short detail line, or empty to update only the fraction.</param>
    void Report(float fraction, string note = "");

    /// <summary>
    /// Publish "done of total" counters for batch work. Preferred over <see cref="Report"/> when the
    /// item count is known, since the display can then show a meaningful "142 / 1035".
    /// </summary>
    /// <param name="done">Items processed so far.</param>
    /// <param name="total">Items in the batch.</param>
    void Units(long done, long total);

    /// <summary>
    /// Declare the combined weight of every phase this operation will open, before opening the
    /// first. Without it the aggregate divides by the phases opened so far, so starting a heavy
    /// phase drags it backwards.
    /// </summary>
    /// <param name="totalWeight">Sum of the weights that will be passed to <see cref="BeginChild"/>.</param>
    void PlanChildren(float totalWeight);

    /// <summary>
    /// Open a nested phase reporting into this operation's aggregate. Dispose it to close the
    /// phase; the returned sink is a no-op one when nesting is unsupported, so callers need no
    /// capability check.
    /// </summary>
    /// <param name="kind">Category of the phase.</param>
    /// <param name="label">Name shown while it runs.</param>
    /// <param name="weight">Its share of the parent, since phases differ wildly in cost.</param>
    IProgressScope BeginChild(BackgroundTaskKind kind, string label, float weight = 1);
}

/// <summary>A nested phase opened by <see cref="IProgressSink.BeginChild"/>.</summary>
public interface IProgressScope : IProgressSink, IDisposable
{
    /// <summary>Close the phase, recording success or failure. Disposing without calling this
    /// records success unless cancellation was requested.</summary>
    /// <param name="ok">Whether the phase succeeded.</param>
    void Finish(bool ok);
}

/// <summary>Discards every report and never reports cancellation.</summary>
public sealed class NullProgressSink : IProgressScope
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullProgressSink Instance = new();

    NullProgressSink()
    {
    }

    /// <inheritdoc />
    public bool IsCancelled => false;

    /// <inheritdoc />
    public void Report(float fraction, string note = "")
    {
    }

    /// <inheritdoc />
    public void Units(long done, long total)
    {
    }

    /// <inheritdoc />
    public void PlanChildren(float totalWeight)
    {
    }

    /// <inheritdoc />
    public IProgressScope BeginChild(BackgroundTaskKind kind, string label, float weight = 1) => Instance;

    /// <inheritdoc />
    public void Finish(bool ok)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
