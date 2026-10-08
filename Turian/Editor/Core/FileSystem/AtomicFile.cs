namespace Turian.Editor.Core;

/// <summary>Publishes complete text files so asset watchers never observe a partially written save.</summary>
static class AtomicFile
{
    /// <summary>Writes beside the destination, then replaces it after the temporary file has closed.</summary>
    public static void WriteAllText(string path, string content)
    {
        var absolute = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(absolute)!,
            $".{Path.GetFileName(absolute)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, absolute, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
