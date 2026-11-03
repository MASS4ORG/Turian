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
            if (File.Exists(absolute))
            {
                // Unlike a move, a replace succeeds on Windows while a reader holds the destination open.
                File.Replace(temporary, absolute, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, absolute);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
