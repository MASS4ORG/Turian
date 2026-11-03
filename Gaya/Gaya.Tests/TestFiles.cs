namespace Gaya.Tests;

/// <summary>Scratch-folder cleanup that works on Windows, where read-only files and loaded assemblies block deletion.</summary>
static class TestFiles
{
    /// <summary>Deletes a folder, clearing read-only flags and leaving behind files still loaded by the process.</summary>
    /// <param name="path">The folder to delete.</param>
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // A loaded assembly cannot be unloaded, so Windows keeps its file; the temp folder is swept later.
        }
    }
}
