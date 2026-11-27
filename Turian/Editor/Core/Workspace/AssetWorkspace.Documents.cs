namespace Turian.Editor.Core;

/// <summary>How the workspace opens contributed documents, and reads and writes the sources behind its tabs.</summary>
public sealed partial class AssetWorkspace
{
    /// <summary>The contributed kind that edits a source file, or null when asset tabs are what opens it.</summary>
    /// <param name="sourcePath">Absolute path of the source file.</param>
    public IWorkspaceDocumentKind? KindFor(string sourcePath) => kinds.KindFor(sourcePath);

    static Asset? LoadMetadata(string sourcePath)
    {
        try
        {
            return Asset.Load(sourcePath + ".meta");
        }
        catch (Exception ex)
        {
            Log.Logger.LogWarning(ex, "No asset metadata could be read for {Path}", sourcePath);
            return null;
        }
    }

    AssetWrapper? OpenContributed(IWorkspaceDocumentKind kind, string path, Guid assetId, bool readOnly)
    {
        if (Find(assetId.ToString()) is { } open)
        {
            Activate(open);
            return open;
        }

        var isReadOnly = readOnly || IsReadOnlySource(path);
        var history = new DocumentHistory();
        IWorkspaceDocument opened;
        try
        {
            opened = kind.Open(new DocumentOpenContext(path, assetId, isReadOnly, history))
                     ?? throw new InvalidOperationException($"{kind.Id} returned no document.");
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Document kind {Kind} could not open {Path}", kind.Id, path);
            Report($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }

        var origin = new ContributedOrigin(kind, path, assetId, readOnly);
        var document = Add(new AssetWrapper(opened, history, isReadOnly, origin));
        Activate(document);
        return document;
    }

    // The asset manager drives the scene tree and inspector, which must let go of the scene behind another tab.
    void DeactivateAssets()
    {
        deactivatingAssets = true;
        try
        {
            assets.ActivateAsset(null);
        }
        finally
        {
            deactivatingAssets = false;
        }
    }

    bool IsReadOnlySource(string? path) =>
        path is not null && services?.GetService<AssetImporter>()?.IsSourceReadOnly(path) == true;

    internal string? SourcePathOf(Asset? asset) =>
        asset is null or TemporaryScene || settings.Settings?.ProjectAbsoluteDir is not { Length: > 0 } project
            ? null
            : Path.GetFullPath(Path.Combine(project, asset.RelativePath));

    internal DocumentSaveResult WriteAsset(AssetWrapper document)
    {
        var asset = document.Asset!;
        assets.SaveAsset(asset);
        return asset.IsModified
            ? DocumentSaveResult.Failed($"{document.Title} could not be written.")
            : DocumentSaveResult.Saved;
    }

    // An asset that is still modified after its write stays open; the asset writers log why. The manager reports
    // success even when a writer marks the asset modified again, so only the asset itself is trusted.
    bool SaveBeforeClose(AssetWrapper document)
    {
        if (document is not { Asset: { } asset, IsReadOnly: false })
        {
            return Save(document).Succeeded;
        }

        assets.SaveAsset(asset);
        return !asset.IsModified;
    }

    // Closing and opening again makes every asset listener drop its cached content and read the source.
    internal void ReloadAsset(AssetWrapper document)
    {
        if (document.Asset is not { } asset || !documents.Contains(document))
        {
            return;
        }

        var previous = Active;
        reloading = document;
        try
        {
            assets.CloseAsset(asset);
            asset.MarkSaved();
            if (document.IsReadOnly)
            {
                assets.SetReadOnly(asset.Id);
            }

            assets.OpenAsset(asset);
        }
        finally
        {
            reloading = null;
        }

        if (previous is not null && !ReferenceEquals(previous, document))
        {
            Activate(previous);
        }
    }

    void Report(string message) =>
        services?.GetService<EditorNotifications>()?.Post(new EditorNotification(message, IsError: true));
}
