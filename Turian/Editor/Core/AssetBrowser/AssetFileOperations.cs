namespace Turian.Editor.Core;

/// <summary>
/// The asset browser's file operations as undoable project steps: renaming, deleting, duplicating and pasting. Deleted
/// and undone entries go to the project's trash with their meta files, so bringing them back keeps their asset ids.
/// </summary>
/// <param name="files">Performs the moves and copies.</param>
/// <param name="undo">Records each operation.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetFileOperations(AssetFileSystem files, UndoService undo)
{
    /// <summary>Raised after an operation, its undo or its redo changed the files, so the browser rescans.</summary>
    public event Action? Changed;

    /// <summary>Renames a file or folder.</summary>
    /// <param name="path">The entry to rename.</param>
    /// <param name="isDirectory">Whether it is a folder.</param>
    /// <param name="newName">The new name.</param>
    /// <returns>The new path, or null when the entry could not be renamed.</returns>
    public string? Rename(string path, bool isDirectory, string newName)
    {
        string? renamed = null;
        Record("Rename",
            () => renamed is null
                ? (renamed = files.Rename(path, isDirectory, newName)) is not null && renamed != path
                : files.MoveTo(path, renamed, isDirectory),
            () => files.MoveTo(renamed!, path, isDirectory));
        return renamed;
    }

    /// <summary>Deletes a file or folder into the project's trash.</summary>
    /// <param name="path">The entry to delete.</param>
    /// <returns>True when it was deleted.</returns>
    public bool Delete(string path)
    {
        var isDirectory = Directory.Exists(path);
        string? trashed = null;
        return Record("Delete",
            () => (trashed = files.MoveToTrash(path)) is not null,
            () => files.MoveTo(trashed!, path, isDirectory));
    }

    /// <summary>Copies a file or folder beside itself; undoing it moves the copy to the trash.</summary>
    /// <param name="path">The entry to copy.</param>
    /// <param name="directory">Where the copy goes.</param>
    /// <param name="isDirectory">Whether it is a folder.</param>
    /// <returns>The copy's path, or null when none was made.</returns>
    public string? Duplicate(string path, string directory, bool isDirectory)
    {
        string? copy = null;
        RecordCreation("Duplicate", () => copy = files.Duplicate(path, directory, isDirectory), isDirectory);
        return copy;
    }

    /// <summary>Pastes the clipboard into a folder; undo trashes a copy, or moves a cut entry back.</summary>
    /// <param name="directory">Where to paste.</param>
    /// <returns>The pasted entry's path, or null when nothing was pasted.</returns>
    public string? Paste(string directory)
    {
        if (files.ClipboardSource is not { } source) return null;

        var isDirectory = Directory.Exists(source);
        string? pasted = null;
        if (!files.ClipboardIsCut)
        {
            RecordCreation("Paste", () => pasted = files.Paste(directory), isDirectory);
            return pasted;
        }

        Record("Paste",
            () => pasted is null
                ? (pasted = files.Paste(directory)) is not null
                : files.MoveTo(source, pasted, isDirectory),
            () => files.MoveTo(pasted!, source, isDirectory));
        return pasted;
    }

    // A new entry: undoing moves it to the trash, redoing brings the same files back with the same asset ids.
    void RecordCreation(string label, Func<string?> create, bool isDirectory)
    {
        string? created = null;
        string? trashed = null;
        Record(label,
            () => created is null
                ? (created = create()) is not null
                : trashed is not null && files.MoveTo(trashed, created, isDirectory),
            () => (trashed = files.MoveToTrash(created!)) is not null);
    }

    // Runs the first attempt; only a successful one becomes a step, whose redo runs the same delegate again.
    bool Record(string label, Func<bool> perform, Func<bool> revert)
    {
        if (!perform())
        {
            Changed?.Invoke();
            return false;
        }

        // A later undo or redo that cannot move the files throws, so the history keeps the step.
        var first = true;
        undo.Perform(label, [],
            () =>
            {
                if (!first && !perform()) throw new IOException($"Could not redo {label}.");
                first = false;
                Changed?.Invoke();
            },
            () =>
            {
                if (!revert()) throw new IOException($"Could not undo {label}.");
                Changed?.Invoke();
            },
            document: UndoService.ProjectDocument);
        return true;
    }
}
