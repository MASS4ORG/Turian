namespace MASS4.Bricks;

/// <summary>Deletes folders holding read-only files, such as git objects, which Windows refuses to delete as they are.</summary>
static class ReadOnlyTree
{
    /// <summary>Deletes a folder and everything under it, clearing read-only flags first.</summary>
    /// <param name="path">The folder to delete.</param>
    public static void Delete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
