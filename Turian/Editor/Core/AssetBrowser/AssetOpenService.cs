namespace Turian.Editor.Core;

/// <summary>
/// Opens an asset the way its kind says it should be opened: as a studio document, in the inspector,
/// in the program the desktop associates with the file, or not at all. The asset browser calls this
/// instead of deciding for itself, so every shell treats a double click alike.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetOpenService(
    AssetTypeCatalog types,
    AssetWorkspace workspace,
    AssetInspectionService inspections,
    NodeInspectorController inspector,
    ILogger log)
{
    /// <summary>Opens what a browser row points at.</summary>
    /// <param name="entry">The row; directories and rows without metadata do nothing.</param>
    /// <returns>What was done.</returns>
    public AssetActivation Open(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.IsDirectory
            ? AssetActivation.None
            : Open(entry.AbsolutePath, entry.AssetMetadata);
    }

    /// <summary>Opens a source file, using its metadata when the studio is the one to edit it.</summary>
    /// <param name="absolutePath">Absolute path of the source file.</param>
    /// <param name="metadata">The file's asset metadata, or null when it has none.</param>
    /// <returns>What was done.</returns>
    public AssetActivation Open(string absolutePath, Asset? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var activation = types.ActivationFor(absolutePath);

        switch (activation)
        {
            case AssetActivation.Edit when metadata is not null:
                workspace.Open(metadata);
                return AssetActivation.Edit;

            case AssetActivation.Inspect:
                inspector.Select(inspections.Inspect(absolutePath));
                return AssetActivation.Inspect;

            case AssetActivation.ExternalProgram:
                OpenExternally(absolutePath);
                return AssetActivation.ExternalProgram;

            default:
                log.LogDebug("{Path} has no open action", absolutePath);
                return AssetActivation.None;
        }
    }

    /// <summary>Hands a file to the program the desktop opens its kind with.</summary>
    /// <param name="absolutePath">Absolute path of the file.</param>
    public void OpenExternally(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        try
        {
            Process.Start(new ProcessStartInfo(absolutePath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No program opened {Path}", absolutePath);
        }
    }

    /// <summary>Shows an asset in the desktop file manager, selecting the file when supported.</summary>
    public void ShowInFileManager(string absolutePath)
    {
        var start = FileManagerCommand(absolutePath);
        try { Process.Start(start)?.Dispose(); }
        catch (Exception ex) { log.LogWarning(ex, "Could not show {Path} in the file manager", absolutePath); }
    }

    /// <summary>Builds a file-manager command with paths kept separate from executable arguments.</summary>
    public static ProcessStartInfo FileManagerCommand(string absolutePath)
    {
        var folder = Directory.Exists(absolutePath) ? absolutePath : Path.GetDirectoryName(absolutePath)!;
        var start = new ProcessStartInfo(FileManagerExecutable());
        if (OperatingSystem.IsMacOS() && File.Exists(absolutePath)) start.ArgumentList.Add("-R");
        start.ArgumentList.Add(OperatingSystem.IsMacOS() ? absolutePath : folder);
        return start;
    }

    static string FileManagerExecutable() => OperatingSystem.IsWindows() ? "explorer.exe"
        : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
}
