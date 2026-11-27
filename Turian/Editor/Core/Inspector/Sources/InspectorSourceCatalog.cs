namespace Turian.Editor.Core;

/// <summary>
/// The sources that describe selections to the Inspector: those registered through dependency injection, then those
/// editor bricks add while they are active. It is used on the UI thread.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class InspectorSourceCatalog
{
    readonly List<IInspectorSource> sources;
    readonly EditorNotifications? notifications;

    /// <summary>Creates the catalog with the sources that stay for its whole lifetime.</summary>
    /// <param name="sources">The sources registered through dependency injection.</param>
    /// <param name="notifications">Where a source's failure is shown to the user.</param>
    public InspectorSourceCatalog(IEnumerable<IInspectorSource>? sources = null,
        EditorNotifications? notifications = null)
    {
        this.sources = [.. sources ?? []];
        this.notifications = notifications;
    }

    // Changes with the set of sources, so a form built before knows to build again.
    internal int Revision { get; private set; }

    /// <summary>Raised after a source was removed.</summary>
    public event Action<IInspectorSource>? Removed;

    /// <summary>Adds a source, such as one a brick contributes while it is active.</summary>
    /// <param name="source">The source.</param>
    /// <returns>The registration; disposing it removes the source.</returns>
    public IDisposable Add(IInspectorSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        sources.Add(source);
        Revision++;
        return new ContributionLifetime(() =>
        {
            if (sources.Remove(source))
            {
                Revision++;
                Removed?.Invoke(source);
            }
        });
    }

    /// <summary>
    /// The source that describes an object: one declaring its exact type, else the first it derives from.
    /// </summary>
    /// <param name="selection">The selected object.</param>
    /// <returns>The source, or null when none describes the object.</returns>
    public IInspectorSource? SourceFor(object selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        IInspectorSource? derived = null;
        foreach (var source in sources)
        {
            if (!Accepts(source, selection))
            {
                continue;
            }

            if (source.SelectionType == selection.GetType())
            {
                return source;
            }

            derived ??= source;
        }

        return derived;
    }

    /// <summary>Whether a source declares a type the object belongs to.</summary>
    /// <param name="source">The source, registered or not.</param>
    /// <param name="selection">The selected object.</param>
    public bool Accepts(IInspectorSource source, object selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        return Query(source.GetType().Name, () => source.SelectionType.IsInstanceOfType(selection));
    }

    /// <summary>
    /// Whether an object is inspected through a source and lives with a document: an <see cref="IInspectorSubject"/>,
    /// or an object a source describes. Scene objects and the studio's own inspections are neither.
    /// </summary>
    /// <param name="selection">The selected object.</param>
    public bool IsSubject(object? selection) => selection switch
    {
        null or Node or Component or AssetInspection or FormInspection => false,
        IInspectorSubject => true,
        _ => SourceFor(selection) is not null,
    };

    /// <summary>Whether a subject still exists; an object without a lifetime of its own always does.</summary>
    /// <param name="subject">The selected object.</param>
    public bool IsAlive(object subject) =>
        subject is not IInspectorSubject live || Query(subject.GetType().Name, () => live.IsAlive);

    /// <summary>Builds the form of the selected subjects that still exist.</summary>
    /// <param name="selection">The selected subjects.</param>
    /// <param name="active">The subject whose source describes the selection; the last one when null.</param>
    /// <returns>
    /// The form, which the caller disposes, or null when there is no subject or no source for it. Its model is empty
    /// when the subjects have different sources or cannot be described.
    /// </returns>
    public InspectorForm? Build(IReadOnlyList<object> selection, object? active = null)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var alive = selection.Distinct(ReferenceEqualityComparer.Instance).Where(IsAlive).ToList();
        var shown = alive.Find(subject => ReferenceEquals(subject, active)) ?? alive.LastOrDefault();
        if (shown is null || SourceFor(shown) is not { } source)
        {
            return null;
        }

        return new InspectorForm(this, alive, Describe(source, alive));
    }

    FormModel Describe(IInspectorSource source, IReadOnlyList<object> subjects)
    {
        if (subjects.Any(subject => !ReferenceEquals(SourceFor(subject), source)))
        {
            return FormModel.Empty;
        }

        try
        {
            var schemas = subjects.Select(source.Describe).OfType<InspectorSchema>().ToList();
            return schemas.Count == subjects.Count
                ? InspectorSchemaForms.Build(subjects, schemas, this)
                : FormModel.Empty;
        }
        catch (Exception ex)
        {
            Report("Could not inspect the selection", ex);
            return FormModel.Empty;
        }
    }

    // Runs a brick's action, reporting a failure instead of letting it reach the UI thread.
    internal bool Run(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Report($"Inspector: {what} failed", ex);
            return false;
        }
    }

    // Asks a brick a question drawn every frame, so a failure is only logged.
    internal bool Query(string what, Func<bool> question)
    {
        try
        {
            return question();
        }
        catch (Exception ex)
        {
            Log.Logger.LogDebug(ex, "The Inspector could not query {What}", what);
            return false;
        }
    }

    void Report(string what, Exception exception)
    {
        Log.Logger.LogError(exception, "{What}", what);
        notifications?.Post(new EditorNotification($"{what}: {exception.Message}", IsError: true));
    }
}
