namespace Turian.Editor.Core;

/// <summary>Copies desktop files or folders into a writable asset directory as one undoable operation.</summary>
public sealed class AssetExternalImport(AssetFileOperations operations)
{
    /// <summary>Imports existing paths, excluding metadata and folders that contain the destination.</summary>
    public bool Import(AssetEntry destination, IEnumerable<string> paths)
    {
        if (!destination.IsDirectory || destination.IsReadOnly || !Directory.Exists(destination.AbsolutePath))
            return false;
        var sources = paths.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Distinct()
            .Select(Source).OfType<AssetEntry>().Where(entry => !ContainsDestination(entry, destination.AbsolutePath));
        return operations.CopyIntoMany(sources, destination.AbsolutePath);
    }

    static AssetEntry? Source(string path)
    {
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        return new AssetEntry(path, Directory.Exists(path), null, Path.GetDirectoryName(path));
    }

    static bool ContainsDestination(AssetEntry source, string destination) => source.IsDirectory
        && (source.AbsolutePath == destination || destination.StartsWith(source.AbsolutePath + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
}
