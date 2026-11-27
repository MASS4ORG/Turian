namespace Turian.Editor.Core;

/// <summary>
/// Describes a selected object to the Inspector as a list of properties, so an editor brick can have its own selections
/// edited there. Register it through dependency injection or <see cref="InspectorSourceCatalog.Add"/>.
/// </summary>
public interface IInspectorSource
{
    /// <summary>The type of selection the source describes, including types derived from it.</summary>
    Type SelectionType { get; }

    /// <summary>Describes one selected object; asked again whenever the Inspector rebuilds.</summary>
    /// <param name="selection">An object of <see cref="SelectionType"/>.</param>
    /// <returns>Its properties, or null when there is nothing to show.</returns>
    InspectorSchema? Describe(object selection);
}

/// <summary>
/// A selection with a lifetime of its own, such as an element of an open document. The studio deselects it when the
/// document that was active while it was selected is left.
/// </summary>
public interface IInspectorSubject
{
    /// <summary>
    /// Identifies what is selected; a subject that starts reporting another key is described again.
    /// </summary>
    string Key { get; }

    /// <summary>Whether the selected thing still exists; the Inspector ignores a subject that does not.</summary>
    bool IsAlive { get; }

    /// <summary>
    /// Raised, from any thread, when the properties to show changed, such as after properties appeared or went away.
    /// Values are read every frame and need no notice.
    /// </summary>
    event Action? Invalidated;
}
