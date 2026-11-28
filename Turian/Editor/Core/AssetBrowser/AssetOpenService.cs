namespace Turian.Editor.Core;

/// <summary>
/// Opens an asset the way <see cref="AssetTypeCatalog"/> says it should be opened: as a studio document, in the
/// inspector, in the program the desktop associates with the file, or not at all. The asset browser calls this instead
/// of deciding for itself, so every shell treats a double click alike.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetOpenService(
    AssetTypeCatalog types,
    AssetWorkspace workspace,
    AssetInspectionService inspections,
    NodeInspectorController inspector,
    CodeOpener code,
    ILogger log,
    AssetImporter? importer = null)
{
    // Starts the desktop program for a file.
    internal Action<ProcessStartInfo> Launch { get; set; } = static start => Process.Start(start)?.Dispose();

    /// <summary>
    /// Whether opening a browser row does anything. A read-only file opens only as a studio document, which shows it
    /// without writing it; it is never handed to a program that could.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <returns>False for directories and read-only files the studio has no document for.</returns>
    public bool CanOpen(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !entry.IsDirectory
               && (!entry.IsReadOnly || types.ActivationFor(entry.AbsolutePath) == AssetActivation.Edit);
    }

    /// <summary>Opens what a browser row points at.</summary>
    /// <param name="entry">The row; one that <see cref="CanOpen"/> refuses does nothing.</param>
    /// <returns>What was done.</returns>
    public AssetActivation Open(AssetEntry entry) =>
        CanOpen(entry)
            ? Open(entry.AbsolutePath, entry.AssetMetadata, entry.IsReadOnly)
            : AssetActivation.None;

    /// <summary>Opens a source file, using its metadata when the studio is the one to edit it.</summary>
    /// <param name="absolutePath">Absolute path of the source file.</param>
    /// <param name="metadata">The file's asset metadata, or null when it has none.</param>
    /// <param name="isReadOnly">Whether the browser entry is read-only.</param>
    /// <returns>What was done.</returns>
    public AssetActivation Open(string absolutePath, Asset? metadata, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var activation = types.ActivationFor(absolutePath);

        switch (activation)
        {
            // A kind that claims the file keeps it, so a document that fails to open does not fall through.
            case AssetActivation.Edit when workspace.KindFor(absolutePath) is not null:
                return OpenDocument(absolutePath, isReadOnly);

            case AssetActivation.Edit when metadata is not null:
                workspace.Open(metadata, isReadOnly);
                return AssetActivation.Edit;

            case AssetActivation.Inspect:
                inspector.Select(inspections.Inspect(absolutePath));
                return AssetActivation.Inspect;

            case AssetActivation.ExternalProgram when IsScript(absolutePath):
                _ = Task.Run(() => code.OpenAsync(absolutePath));
                return AssetActivation.ExternalProgram;

            case AssetActivation.ExternalProgram:
                log.LogInformation(
                    "No studio editor claims {Path}, so the system program opens it; a brick's editor is missing when "
                    + "its brick or its code failed to load", absolutePath);
                OpenExternally(absolutePath);
                return AssetActivation.ExternalProgram;

            default:
                log.LogDebug("{Path} has no open action", absolutePath);
                return AssetActivation.None;
        }
    }

    /// <summary>
    /// Imports a file that was just created, so it has its metadata, then opens it the way a double click does.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the new asset; a folder does nothing.</param>
    /// <returns>What was done.</returns>
    public AssetActivation OpenCreated(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!File.Exists(absolutePath))
        {
            return AssetActivation.None;
        }

        try
        {
            importer?.ReimportNow(absolutePath);
            var metaPath = absolutePath + ".meta";
            return Open(absolutePath, File.Exists(metaPath) ? Asset.Load(metaPath) : null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The new asset {Path} could not be opened", absolutePath);
            return AssetActivation.None;
        }
    }

    AssetActivation OpenDocument(string absolutePath, bool isReadOnly)
    {
        var readOnly = isReadOnly || importer?.IsSourceReadOnly(absolutePath) == true;
        return workspace.Open(absolutePath, readOnly) is null ? AssetActivation.None : AssetActivation.Edit;
    }

    static bool IsScript(string path) => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hands a file to the program the desktop opens its kind with.</summary>
    /// <param name="absolutePath">Absolute path of the file.</param>
    public void OpenExternally(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        try
        {
            Launch(new ProcessStartInfo(absolutePath) { UseShellExecute = true });
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
        if (OperatingSystem.IsMacOS() && File.Exists(absolutePath))
        {
            start.ArgumentList.Add("-R");
        }

        start.ArgumentList.Add(OperatingSystem.IsMacOS() ? absolutePath : folder);
        return start;
    }

    static string FileManagerExecutable() => OperatingSystem.IsWindows() ? "explorer.exe"
        : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
}
