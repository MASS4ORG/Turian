namespace Turian.Editor.Core;

public static partial class CsProjectGenerator
{
    // What the running editor offers editor code: the plugin and panel contracts and the widgets they draw with.
    static readonly string[] EditorHostLibraries = ["Gaya.Sdk", "Autoformers", "Guinevere", "Guinevere.Excalibur"];

    /// <summary>
    /// References the editor libraries found in <paramref name="hostDirectory"/>, so editor code compiled from sources
    /// can contribute panels, documents and plugins. A library the project already references or takes as a package is
    /// left to that reference.
    /// </summary>
    /// <param name="projectRoot">The editor-only assembly's project, with its other references already added.</param>
    /// <param name="hostDirectory">The running editor's folder; a host without these libraries adds none.</param>
    internal static void AddEditorHostReferences(ProjectRootElement projectRoot, string hostDirectory)
    {
        var taken = projectRoot.Items
            .Where(static item => item.ItemType is "Reference" or "PackageReference")
            .Select(static item => item.Include)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = projectRoot.AddItemGroup();
        foreach (var name in EditorHostLibraries)
        {
            var path = Path.Combine(hostDirectory, $"{name}.dll");
            if (!taken.Contains(name) && !taken.Contains($"MASS4.{name}") && File.Exists(path))
            {
                references.AddItem("Reference", name).AddMetadata("HintPath", path, true);
            }
        }
    }
}
