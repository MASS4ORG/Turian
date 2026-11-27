namespace Turian.Editor.Core;

/// <summary>
/// The form of the selected subjects, which follows them until it is disposed and says when it has to be built again.
/// </summary>
public sealed class InspectorForm : IDisposable
{
    readonly InspectorSourceCatalog catalog;
    readonly List<(object Subject, string Key)> subjects;
    readonly int revision;
    volatile bool invalidated;

    internal InspectorForm(InspectorSourceCatalog catalog, IReadOnlyList<object> selection, FormModel model)
    {
        this.catalog = catalog;
        Model = model;
        revision = catalog.Revision;
        subjects = [.. selection.Select(subject => (subject, KeyOf(subject)))];
        foreach (var subject in selection.OfType<IInspectorSubject>())
        {
            catalog.Run(subject.GetType().Name, () => subject.Invalidated += OnInvalidated);
        }
    }

    /// <summary>The sections to draw.</summary>
    public FormModel Model { get; }

    /// <summary>
    /// Whether the model is out of date: a subject reported a change, died or took another key, or the sources changed.
    /// </summary>
    public bool IsStale => invalidated || revision != catalog.Revision || subjects.Any(Outdated);

    bool Outdated((object Subject, string Key) entry) =>
        !catalog.IsAlive(entry.Subject) || KeyOf(entry.Subject) != entry.Key;

    /// <summary>Stops following the subjects.</summary>
    public void Dispose()
    {
        foreach (var subject in subjects.Select(static entry => entry.Subject).OfType<IInspectorSubject>())
        {
            catalog.Run(subject.GetType().Name, () => subject.Invalidated -= OnInvalidated);
        }
    }

    void OnInvalidated() => invalidated = true;

    static string KeyOf(object subject)
    {
        try
        {
            return subject is IInspectorSubject live ? live.Key : "";
        }
        catch (Exception ex)
        {
            Log.Logger.LogDebug(ex, "A subject could not name itself");
            return "";
        }
    }
}
