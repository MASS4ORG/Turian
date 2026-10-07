namespace Turian.Editor.Core;

/// <summary>Preserves generated project timestamps when their contents are unchanged.</summary>
static class GeneratedProject
{
    internal static void Save(ProjectRootElement project)
    {
        var content = project.RawXml;
        if (File.Exists(project.FullPath) && File.ReadAllText(project.FullPath) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(project.FullPath)!);
        File.WriteAllText(project.FullPath, content);
    }
}
