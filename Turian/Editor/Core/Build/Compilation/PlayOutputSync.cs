namespace Turian.Editor.Core;

/// <summary>Copies imported runtime files when their source timestamps or lengths change.</summary>
static class PlayOutputSync
{
    internal static void CopyFile(string source, string destination)
    {
        var input = new FileInfo(source);
        var output = new FileInfo(destination);
        if (output.Exists && input.Length == output.Length && input.LastWriteTimeUtc == output.LastWriteTimeUtc) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
