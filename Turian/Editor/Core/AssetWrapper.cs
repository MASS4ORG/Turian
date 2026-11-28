namespace Turian.Editor.Core;

/// <summary>
/// One workspace tab: either an asset edited through the asset manager, or a document a brick contributed. It exposes
/// UI-friendly metadata and applies the save rules every kind of document shares.
/// </summary>
public sealed class AssetWrapper : IWorkspaceDocument, IEquatable<AssetWrapper>, INotifyPropertyChanged
{
    readonly IWorkspaceDocument? contributed;
    readonly AssetWorkspace? owner;
    bool openedReadOnly;

    /// <summary>
    /// Gets the asset behind an asset tab, or null for a contributed document.
    /// </summary>
    public Asset? Asset { get; }

    /// <summary>The document an editor brick contributed, or null for an asset tab.</summary>
    public IWorkspaceDocument? Document => contributed;

    /// <summary>Where the document's edits are recorded, which Undo and Redo act on while the tab is active.</summary>
    public IDocumentHistory History { get; }

    /// <summary>How a contributed document was opened, so it can be opened again; null for an asset tab.</summary>
    internal ContributedOrigin? Origin { get; }

    /// <inheritdoc />
    public string Id => contributed?.Id ?? Asset?.Id.ToString() ?? string.Empty;

    /// <inheritdoc />
    public string Kind => contributed?.Kind
                          ?? (Asset is Prefab ? WorkspaceDocumentKinds.Scene : WorkspaceDocumentKinds.Asset);

    /// <summary>
    /// Gets the base title shown for the asset tab.
    /// </summary>
    public string Title => contributed?.Title
                           ?? (string.IsNullOrWhiteSpace(Asset?.DisplayName) ? "<assets>" : Asset.DisplayName);

    /// <inheritdoc />
    public string? SourcePath => contributed is null ? owner?.SourcePathOf(Asset) : contributed.SourcePath;

    /// <summary>
    /// Gets whether the document has unsaved changes.
    /// </summary>
    public bool IsDirty => contributed?.IsDirty ?? Asset?.IsModified == true;

    /// <inheritdoc />
    public bool IsReadOnly => openedReadOnly || contributed?.IsReadOnly == true;

    /// <inheritdoc />
    public string? Conflict => contributed?.Conflict;

    /// <summary>
    /// Gets the title shown in the tab, including the dirty marker.
    /// </summary>
    public string DisplayTitle => IsDirty ? $"*{Title}" : Title;

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetWrapper"/> class outside any workspace, so it cannot save.
    /// </summary>
    /// <param name="asset">The asset to wrap.</param>
    public AssetWrapper(Asset? asset)
        : this(asset, owner: null, new DocumentHistory())
    {
    }

    internal AssetWrapper(Asset? asset, AssetWorkspace? owner, IDocumentHistory history)
    {
        Asset = asset;
        History = history;
        this.owner = owner;

        if (Asset is not null)
        {
            Asset.StateChanged += HandleAssetStateChanged;
        }
    }

    internal AssetWrapper(IWorkspaceDocument document, IDocumentHistory history, bool readOnly,
        ContributedOrigin origin)
    {
        contributed = document;
        History = history;
        openedReadOnly = readOnly;
        Origin = origin;
        document.Changed += RefreshFromAsset;
    }

    void HandleAssetStateChanged(Asset _) => RefreshFromAsset();

    /// <summary>
    /// Tells listeners to read the tab's state again.
    /// </summary>
    public void RefreshFromAsset()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
        Changed?.Invoke();
    }

    internal void OpenReadOnly()
    {
        openedReadOnly = true;
        RefreshFromAsset();
    }

    /// <summary>
    /// Writes the tab's edits, refusing a read-only source, and a conflicted one unless asked to overwrite it.
    /// </summary>
    /// <param name="overwrite">Whether to replace a source that conflicts with the edits.</param>
    /// <returns>Whether the source was written, and why not.</returns>
    public DocumentSaveResult Save(bool overwrite = false)
    {
        if (IsReadOnly)
        {
            return IsDirty ? DocumentSaveResult.Failed($"{Title} is read-only.") : DocumentSaveResult.Saved;
        }

        if (contributed is not null)
        {
            return SaveContributed(contributed, overwrite);
        }

        return owner?.WriteAsset(this) ?? DocumentSaveResult.Failed($"{Title} is not open in a workspace.");
    }

    // A brick's failure, whatever it throws, leaves the document dirty instead of reaching the UI thread.
    static DocumentSaveResult SaveContributed(IWorkspaceDocument document, bool overwrite)
    {
        try
        {
            if (!document.IsDirty)
            {
                return DocumentSaveResult.Saved;
            }

            if (document.Conflict is { } conflict && !overwrite)
            {
                return DocumentSaveResult.Failed(conflict);
            }

            var result = document.Save(overwrite) ?? DocumentSaveResult.Failed("The save returned no result.");

            // A document that says it was written but is still dirty must not be closed or left behind.
            return result.Succeeded && document.IsDirty
                ? DocumentSaveResult.Failed($"{document.Title} still has unsaved changes after saving.")
                : result;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "A contributed document could not be saved");
            return DocumentSaveResult.Failed(Reason(ex));
        }
    }

    /// <summary>
    /// Drops the unsaved edits, reads the source again and forgets the steps that led to them. A contributed document
    /// that cannot read its source is logged and keeps its edits.
    /// </summary>
    public void Reload() => TryReload();

    /// <summary>Reloads the tab and says why it could not.</summary>
    /// <returns>Null when the source was read again.</returns>
    internal string? TryReload()
    {
        try
        {
            if (contributed is null)
            {
                owner?.ReloadAsset(this);
                return null;
            }

            contributed.Reload();
            History.Clear();
            RefreshFromAsset();
            return null;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "A document could not be reloaded");
            return Reason(ex);
        }
    }

    static string Reason(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    /// <inheritdoc/>
    public bool Equals(AssetWrapper? other) =>
        other is not null && Asset == other.Asset && ReferenceEquals(contributed, other.contributed);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj switch
    {
        AssetWrapper other => Equals(other),
        _ => false,
    };

    /// <inheritdoc/>
    public override int GetHashCode() => Asset?.GetHashCode() ?? contributed?.GetHashCode() ?? 0;

    /// <summary>Stops following the asset, or closes the contributed document.</summary>
    public void Dispose()
    {
        if (Asset is not null)
        {
            Asset.StateChanged -= HandleAssetStateChanged;
        }

        if (contributed is not null)
        {
            Release(contributed);
        }
    }

    void Release(IWorkspaceDocument document)
    {
        try
        {
            document.Changed -= RefreshFromAsset;
            document.Dispose();
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "A contributed document failed to close");
        }
    }

    void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
