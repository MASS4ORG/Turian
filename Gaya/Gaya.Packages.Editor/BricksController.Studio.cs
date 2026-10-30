namespace Gaya.Packages.Editor;

public sealed partial class BricksController
{
    /// <summary>Whether a successful Studio brick change needs an application restart to activate plugin code.</summary>
    public bool StudioRestartRequired { get; private set; }

    void ApplyWork(IBrickWorkspace workspace, Action<IBrickWorkspace> work, bool apply)
    {
        var previous = apply && ReferenceEquals(workspace, studio) ? StudioCode(workspace.Resolve()) : null;
        work(workspace);
        if (previous is not null && previous != StudioCode(workspace.Resolve()))
        {
            StudioRestartRequired = true;
        }
    }

    static string StudioCode(IEnumerable<ResolvedPackage> bricks) => string.Join('\n', bricks
        .SelectMany(StudioBricks.LibraryDirectories).Where(Directory.Exists)
        .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.dll"))
        .Order(StringComparer.Ordinal).Select(static path =>
            $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{new FileInfo(path).Length}"));
}
