namespace Turian.Editor.Core;

/// <summary>A breadcrumb's folder path and display name.</summary>
public sealed record AssetPathSegment(string Path, string Name);

/// <summary>Keeps bounded folder history and derives breadcrumbs from scanned directory roots.</summary>
public sealed class AssetBrowserNavigation
{
    readonly List<string> history = [];
    int position = -1;

    /// <summary>The active folder, or null before a project is opened.</summary>
    public string? Current => position < 0 ? null : history[position];

    /// <summary>Whether a prior folder can be visited.</summary>
    public bool CanBack => position > 0;

    /// <summary>Whether a subsequent folder can be visited.</summary>
    public bool CanForward => position + 1 < history.Count;

    /// <summary>Starts navigation for a project.</summary>
    public void Reset(string root)
    {
        history.Clear();
        position = -1;
        Visit(root);
    }

    /// <summary>Visits a different folder and replaces the forward history.</summary>
    public void Visit(string folder)
    {
        if (Current == folder) return;
        history.RemoveRange(position + 1, history.Count - position - 1);
        history.Add(folder);
        if (history.Count > 32) history.RemoveAt(0);
        position = history.Count - 1;
    }

    /// <summary>Moves backward when a prior folder exists.</summary>
    public void Back()
    {
        if (CanBack) position--;
    }

    /// <summary>Moves forward when a subsequent folder exists.</summary>
    public void Forward()
    {
        if (CanForward) position++;
    }

    /// <summary>Remaps history after a move, including descendants of a moved folder.</summary>
    public void Remap(string source, string destination)
    {
        for (var i = 0; i < history.Count; i++) history[i] = AssetPathSegments.Remap(history[i], source, destination);
    }
}

/// <summary>Builds trails using the scanned tree so brick roots never escape into unrelated directories.</summary>
public static class AssetPathSegments
{
    /// <summary>Returns an ancestor trail, root first.</summary>
    public static IReadOnlyList<AssetPathSegment> Build(string folder, IReadOnlyList<AssetEntry> entries)
    {
        var trail = new List<AssetPathSegment>();
        var byPath = entries.Where(entry => entry.IsDirectory).ToDictionary(entry => entry.AbsolutePath);
        for (var path = folder; path is not null && byPath.TryGetValue(path, out var entry); path = entry.ParentPath)
            trail.Add(new AssetPathSegment(path, Path.GetFileName(path)));
        trail.Reverse();
        return trail;
    }

    /// <summary>Replaces a moved path prefix without matching neighboring folder names.</summary>
    public static string Remap(string path, string source, string destination) => path == source
        ? destination
        : path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? destination + path[source.Length..] : path;
}
