namespace Turian.Editor.Core;

/// <summary>The external editors Studio can use: Studio's own and those bricks register, resolved by stable id.</summary>
public interface IExternalEditorRegistry
{
    /// <summary>Raised after an editor is added or removed.</summary>
    event Action? Changed;

    /// <summary>Every registered editor, in registration order.</summary>
    IReadOnlyList<IExternalEditor> Editors { get; }

    /// <summary>Registers an editor; one with the same id is replaced.</summary>
    /// <param name="editor">The editor to add.</param>
    void Register(IExternalEditor editor);

    /// <summary>
    /// Removes the editor registered with this id. A default Studio ships cannot be removed, only replaced by
    /// registering an editor with its id, and comes back when that editor is unregistered.
    /// </summary>
    /// <param name="id">The editor's id.</param>
    /// <returns><c>true</c> when a registered editor was removed.</returns>
    bool Unregister(string id);

    /// <summary>Finds an editor by id, ignoring case.</summary>
    /// <param name="id">The editor's id.</param>
    /// <returns>The editor, or <c>null</c> when none has that id.</returns>
    IExternalEditor? Find(string id);

    /// <summary>The installations of every editor, grouped by editor in registration order.</summary>
    /// <returns>What the editor dropdown lists; an editor whose discovery fails contributes nothing.</returns>
    IReadOnlyList<EditorInstallation> DiscoverAll();
}
