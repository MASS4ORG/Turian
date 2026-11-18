using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gaya.Storage;

/// <summary>
/// One JSON settings file keyed by page id: read once, changed member by member, and written back atomically with a
/// backup. The studio and headless tools share it, so a scope's file behaves the same wherever it is written.
/// </summary>
/// <param name="path">The file's absolute path.</param>
/// <param name="report">Receives a message and its cause when the file cannot be read or written; null reports nothing.</param>
public sealed class SettingsFile(string path, Action<string, Exception>? report = null)
{
    JsonObject? stored;
    bool observed;
    (DateTime Written, long Length)? seen;

    /// <summary>The options every settings file is read and written with.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The file this instance reads and writes.</summary>
    public string Path { get; } = path;

    /// <summary>The file's contents, read once and kept, or null when there is nothing usable.</summary>
    /// <returns>The document, which callers must not change.</returns>
    public JsonObject? Load()
    {
        if (stored is not null)
        {
            return stored;
        }

        try
        {
            stored = ReadDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            report?.Invoke($"Settings: could not be read from {Path}", ex);
        }

        return stored;
    }

    /// <summary>Reads the file as it is now, bypassing what <see cref="Load"/> kept.</summary>
    /// <returns>The document, or null when the file does not exist.</returns>
    /// <exception cref="JsonException">The file does not hold an object keyed by page id.</exception>
    public JsonObject? ReadDocument()
    {
        observed = true;
        seen = Stamp();
        return !File.Exists(Path) ? null
            : JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
              ?? throw new JsonException("Settings must contain an object keyed by page id.");
    }

    /// <summary>Writes a document through a temporary file, keeping the previous contents as a backup.</summary>
    /// <param name="document">What the file should hold from now on.</param>
    public void WriteDocument(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(JsonOptions));
            if (File.Exists(Path))
            {
                File.Copy(Path, $"{Path}.bak", overwrite: true);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        stored = document;
        observed = true;
        seen = Stamp();
    }

    /// <summary>Whether the file holds a value for a member of a page.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name; matched without regard to case.</param>
    public bool Has(string pageId, string member) => Find(pageId, member) is not null;

    /// <summary>The JSON the file holds for a member of a page, or null when it holds none.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name.</param>
    public JsonNode? Get(string pageId, string member) =>
        Find(pageId, member) is { } key ? Load()![pageId]![key]?.DeepClone() : null;

    /// <summary>
    /// Changes individual members of one page in the latest contents of the file and writes it back. A file left
    /// holding nothing is deleted with its backup and its folder, so no empty settings file is ever kept.
    /// </summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="set">Members to store, by name.</param>
    /// <param name="remove">Members to take out, by name.</param>
    /// <returns>Whether the file now holds the change; a file that cannot be read or written is left as it was.</returns>
    public bool Update(string pageId, IReadOnlyDictionary<string, JsonNode?> set, IReadOnlyCollection<string> remove)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);

        if (set.Count == 0 && remove.Count == 0)
        {
            return true;
        }

        try
        {
            var document = ReadDocument() ?? new JsonObject();
            ChangePage(document, pageId, set, remove);
            if (document.Count == 0)
            {
                DeleteFiles();
                stored = document;
            }
            else
            {
                WriteDocument(document);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            report?.Invoke($"Settings: could not be saved to {Path}", ex);
            return false;
        }
    }

    /// <summary>Whether something else changed the file since this instance last read or wrote it.</summary>
    /// <returns><c>true</c> when the file's timestamp or size differs from the last observation.</returns>
    public bool ChangedOnDisk() => observed && Stamp() != seen;

    /// <summary>Forgets what was read, so the next <see cref="Load"/> reads the file again.</summary>
    public void Invalidate()
    {
        stored = null;
        observed = false;
    }

    string? Find(string pageId, string member) =>
        Load()?[pageId] is JsonObject values
            ? values.Select(static pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, member, StringComparison.OrdinalIgnoreCase))
            : null;

    (DateTime Written, long Length)? Stamp() =>
        File.Exists(Path) ? (File.GetLastWriteTimeUtc(Path), new FileInfo(Path).Length) : null;

    void DeleteFiles()
    {
        // File.Delete throws when the folder itself is gone, which is how a scope that never held anything looks.
        foreach (var file in new[] { Path, $"{Path}.bak" }.Where(File.Exists))
        {
            File.Delete(file);
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }

        observed = true;
        seen = null;
    }

    static void ChangePage(JsonObject document, string pageId, IReadOnlyDictionary<string, JsonNode?> set,
        IReadOnlyCollection<string> remove)
    {
        var values = document[pageId] as JsonObject ?? new JsonObject();
        foreach (var name in remove.Concat(set.Keys))
        {
            foreach (var key in values.Select(static pair => pair.Key)
                         .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                values.Remove(key);
            }
        }

        foreach (var (name, value) in set)
        {
            values[name] = value?.DeepClone();
        }

        if (values.Count == 0)
        {
            document.Remove(pageId);
        }
        else
        {
            document[pageId] = values;
        }
    }
}
