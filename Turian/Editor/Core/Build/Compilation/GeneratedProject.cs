namespace Turian.Editor.Core;

/// <summary>Preserves generated project timestamps when their contents are unchanged.</summary>
static class GeneratedProject
{
    /// <summary>Writes the project unless the file already holds the same content.</summary>
    /// <param name="project">The generated project.</param>
    /// <returns><c>true</c> when the file was written.</returns>
    internal static bool Save(ProjectRootElement project) => SaveText(project.FullPath, project.RawXml);

    /// <summary>Writes the text unless the file already holds it.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">The text.</param>
    /// <returns><c>true</c> when the file was written.</returns>
    internal static bool SaveText(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }
}
