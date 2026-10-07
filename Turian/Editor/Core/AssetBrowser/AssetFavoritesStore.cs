using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>Stores favorites privately for each project, including paths moved by undo and redo.</summary>
public sealed class AssetFavoritesStore
{
    readonly string project;
    readonly string file;
    HashSet<string> paths = new(StringComparer.Ordinal);

    /// <summary>Loads favorites from Gaya's user directory or an explicitly supplied test directory.</summary>
    public AssetFavoritesStore(string projectRoot, string? configDirectory = null)
    {
        project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var key = OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        file = Path.Combine(configDirectory ?? GayaConfig.Directory, "favorites", hash + ".json");
        Load();
    }

    /// <summary>Favorite absolute paths, suitable for browser queries.</summary>
    public IReadOnlySet<string> Paths => paths;

    /// <summary>Adds or removes a favorite and persists it.</summary>
    public void Set(string absolutePath, bool favorite)
    {
        if (favorite) paths.Add(absolutePath);
        else paths.Remove(absolutePath);
        Save();
    }

    /// <summary>Remaps a favorite and its descendants after a file operation.</summary>
    public void Remap(string source, string destination)
    {
        paths = paths.Select(path => AssetPathSegments.Remap(path, source, destination))
            .ToHashSet(StringComparer.Ordinal);
        Save();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            var relative = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [];
            paths = relative.Select(path => Path.GetFullPath(Path.Combine(project, path)))
                .Where(path => File.Exists(path) || Directory.Exists(path)).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            paths.Clear();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths.Select(path => Path.GetRelativePath(project, path))));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Logger.LogWarning(ex, "Could not save asset favorites");
        }
    }
}
