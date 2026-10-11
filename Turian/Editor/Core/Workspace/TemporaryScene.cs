namespace Turian.Editor.Core;

/// <summary>A scene backed by a disposable prefab file outside the project's asset catalog.</summary>
public sealed class TemporaryScene : Prefab, IDisposable
{
    readonly DirectoryInfo directory;

    /// <summary>Creates an empty hierarchy in a private temporary folder.</summary>
    /// <param name="name">The scene and tab name.</param>
    public TemporaryScene(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        directory = Directory.CreateTempSubdirectory("turian-scene-");
        RelativePath = Path.Combine(directory.FullName, name + ".prefab");
        File.WriteAllText(RelativePath, Serializer.Serialize(new Node { Name = name }));
    }

    /// <summary>Deletes the scene's temporary backing files.</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory.FullName)) directory.Delete(recursive: true);
    }
}
