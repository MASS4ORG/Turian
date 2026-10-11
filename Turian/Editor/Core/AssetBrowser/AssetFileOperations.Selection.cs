namespace Turian.Editor.Core;

public sealed partial class AssetFileOperations
{
    sealed record FileEdit(Func<bool> Apply, Func<bool> Revert);

    /// <summary>Deletes selected files and folders to the trash as one undo step, skipping nested selections.</summary>
    public bool DeleteMany(IEnumerable<AssetEntry> selection) => RecordBatch("Delete", Roots(selection).Select(entry =>
    {
        string? trash = null;
        return new FileEdit(() => (trash = files.MoveToTrash(entry.AbsolutePath)) is not null,
            () => files.MoveTo(trash!, entry.AbsolutePath, entry.IsDirectory));
    }));

    /// <summary>Moves selected entries into a folder with their metadata as one undoable operation.</summary>
    public bool MoveMany(IEnumerable<AssetEntry> selection, string directory)
    {
        var entries = Roots(selection).ToArray();
        if (entries.Any(entry => Inside(entry.AbsolutePath, directory))) return false;
        return RecordBatch("Move", entries.Where(entry => Path.GetDirectoryName(entry.AbsolutePath) != directory)
            .Select(entry =>
            {
                string? destination = null;
                return new FileEdit(() =>
                {
                    destination ??= AvailablePath(directory, Path.GetFileName(entry.AbsolutePath));
                    return files.MoveTo(entry.AbsolutePath, destination, entry.IsDirectory);
                }, () => files.MoveTo(destination!, entry.AbsolutePath, entry.IsDirectory));
            }));
    }

    /// <summary>Copies selected entries beside themselves, restoring the same copied asset ids after undo.</summary>
    public bool DuplicateMany(IEnumerable<AssetEntry> selection) => RecordBatch("Duplicate", Roots(selection).Select(entry =>
    {
        string? created = null;
        string? trash = null;
        return new FileEdit(() => created is null
            ? (created = files.Duplicate(entry.AbsolutePath, Path.GetDirectoryName(entry.AbsolutePath)!,
                entry.IsDirectory)) is not null
            : files.MoveTo(trash!, created, entry.IsDirectory),
            () => (trash = files.MoveToTrash(created!)) is not null);
    }));

    /// <summary>Copies selected entries into a destination as one undo step.</summary>
    public bool CopyIntoMany(IEnumerable<AssetEntry> selection, string directory) =>
        RecordBatch("Paste", CopyRoots(selection).Select(entry =>
        {
            string? created = null;
            string? trash = null;
            return new FileEdit(() => created is null
                ? (created = files.Duplicate(entry.AbsolutePath, directory, entry.IsDirectory)) is not null
                : files.MoveTo(trash!, created, entry.IsDirectory),
                () => (trash = files.MoveToTrash(created!)) is not null);
        }));

    static IEnumerable<AssetEntry> CopyRoots(IEnumerable<AssetEntry> selection)
    {
        var entries = selection.Where(entry => entry.ParentPath is not null).DistinctBy(entry => entry.AbsolutePath).ToArray();
        return entries.Where(entry => !entries.Any(parent => parent.IsDirectory && parent != entry
            && Inside(parent.AbsolutePath, entry.AbsolutePath)));
    }

    /// <summary>Returns writable entries whose selected parent directories already cover no other selected entry.</summary>
    public static IReadOnlyList<AssetEntry> Roots(IEnumerable<AssetEntry> selection)
    {
        var entries = selection.Where(entry => !entry.IsReadOnly && entry.ParentPath is not null)
            .DistinctBy(entry => entry.AbsolutePath).ToArray();
        return [.. entries.Where(entry => !entries.Any(parent => parent.IsDirectory
            && parent.AbsolutePath != entry.AbsolutePath && Inside(parent.AbsolutePath, entry.AbsolutePath)))];
    }

    bool RecordBatch(string label, IEnumerable<FileEdit> edits)
    {
        var actions = edits.ToArray();
        if (actions.Length == 0) return false;
        return Record(label, () => ApplyBatch(actions), () => RevertBatch(actions));
    }

    static bool ApplyBatch(IReadOnlyList<FileEdit> actions)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i].Apply()) continue;
            for (var done = i - 1; done >= 0; done--) actions[done].Revert();
            return false;
        }
        return true;
    }

    static bool RevertBatch(IReadOnlyList<FileEdit> actions)
    {
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            if (actions[i].Revert()) continue;
            for (var done = i + 1; done < actions.Count; done++) actions[done].Apply();
            return false;
        }
        return true;
    }

    static bool Inside(string directory, string path) => path == directory
        || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    static string AvailablePath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        var index = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} {index++}{Path.GetExtension(name)}");
        return candidate;
    }
}
