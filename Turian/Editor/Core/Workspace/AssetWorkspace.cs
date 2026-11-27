namespace Turian.Editor.Core;

/// <summary>
/// The documents currently open for editing, in tab order, with one of them active: assets, and the documents editor
/// bricks contribute. UI-agnostic: a shell renders <see cref="Documents"/> and reacts to the events; every decision
/// that is policy rather than presentation — what closing a dirty document means, whether play mode is available —
/// lives here.
/// </summary>
public sealed partial class AssetWorkspace : IDisposable
{
    readonly AssetManager assets;
    readonly SettingsService settings;
    readonly IServiceProvider? services;
    readonly WorkspaceDocumentKindRegistry kinds;
    readonly AssetTypeCatalog types;
    readonly List<AssetWrapper> documents = [];
    AssetWrapper? reloading;
    bool deactivatingAssets;
    bool closingAll;
    int newSceneNumber;

    /// <summary>Creates a workspace over the asset manager, resetting when a project is loaded.</summary>
    /// <param name="assets">The asset manager this workspace drives.</param>
    /// <param name="settings">Watched so opening a project clears the previous one's documents.</param>
    /// <param name="kinds">The document kinds to offer when <paramref name="services"/> holds no kind registry.</param>
    /// <param name="services">
    /// Supplies the live document kind registry and, when first needed, the project's undo service, asset types,
    /// read-only policy and notifications. Without it, each tab keeps its own undo steps.
    /// </param>
    public AssetWorkspace(AssetManager assets, SettingsService settings,
        IEnumerable<IWorkspaceDocumentKind>? kinds = null, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(settings);

        this.assets = assets;
        this.settings = settings;
        this.services = services;
        this.kinds = services?.GetService<WorkspaceDocumentKindRegistry>() ?? new WorkspaceDocumentKindRegistry(kinds);
        types = services?.GetService<AssetTypeCatalog>() ?? new AssetTypeCatalog([], this.kinds);

        this.kinds.Added += OnKindAdded;
        this.kinds.Removed += OnKindRemoved;
        assets.AssetOpened += OnAssetOpened;
        assets.AssetClosed += OnAssetClosed;
        assets.AssetSaved += _ => Changed?.Invoke();
        assets.DirtyStateChanged += _ => Changed?.Invoke();
        settings.SettingsLoaded += _ => CloseAll();
    }

    /// <summary>The open documents, in the order they were opened.</summary>
    public IReadOnlyList<AssetWrapper> Documents => documents;

    /// <summary>The document being edited, or null when nothing is open.</summary>
    public AssetWrapper? Active { get; private set; }

    /// <summary>Whether any open document has unsaved edits.</summary>
    public bool HasUnsavedChanges => documents.Any(static document => document.IsDirty);

    /// <summary>
    /// Whether a scene is open, which is what play mode needs. A shell binds its Play button to this.
    /// </summary>
    public bool HasOpenScene => documents.Any(static document => document.Asset is Prefab);

    /// <summary>Raised when a document joins the workspace.</summary>
    public event Action<AssetWrapper>? Opened;

    /// <summary>Raised when a document leaves the workspace.</summary>
    public event Action<AssetWrapper>? Closed;

    /// <summary>Raised when the active document changes, with null when nothing is left open.</summary>
    public event Action<AssetWrapper?>? Activated;

    /// <summary>
    /// Raised whenever anything a tab strip draws may have changed — the document list, the active
    /// one, a dirty marker or a conflict.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Opens an asset, or activates it when it is already open. An asset whose source a contributed kind claims opens
    /// as that kind's document, and one inside an installed brick opens read-only.
    /// </summary>
    /// <param name="asset">The asset to open.</param>
    /// <param name="readOnly">Whether the source may not be written, for a document opened by this call.</param>
    /// <returns>The document for it.</returns>
    public AssetWrapper Open(Asset asset, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // A contributed document stays the tab for its asset, even after its kind went away.
        if (Find(asset) is { Document: not null } open)
        {
            Activate(open);
            return open;
        }

        var path = SourcePathOf(asset);
        if (path is not null && KindFor(path) is { } kind
            && OpenContributed(kind, path, asset.Id, readOnly) is { } contributed)
        {
            return contributed;
        }

        // Marked before the asset opens, so nothing that follows it can change it.
        if (Find(asset) is null && (readOnly || IsReadOnlySource(path)))
        {
            assets.SetReadOnly(asset.Id);
        }

        assets.OpenAsset(asset);
        var document = Find(asset) ?? Track(asset);
        if (assets.IsReadOnly(asset.Id))
        {
            document.OpenReadOnly();
        }

        return document;
    }

    /// <summary>
    /// Opens a source file as a contributed document, or as an asset tab when the studio edits its asset type. A
    /// source that is already open is only activated, and keeps whether it may be written.
    /// </summary>
    /// <param name="sourcePath">Path of the source file, which needs its asset metadata beside it.</param>
    /// <param name="readOnly">Whether the source may not be written, for a document opened by this call.</param>
    /// <returns>The document, or null when nothing here opens the file, so another program may.</returns>
    public AssetWrapper? Open(string sourcePath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var path = Path.GetFullPath(sourcePath);
        if (documents.Find(tab => tab.Origin?.Path == path) is { } contributed)
        {
            Activate(contributed);
            return contributed;
        }

        var kind = KindFor(path);
        if ((kind is null && types.ActivationFor(path) != AssetActivation.Edit)
            || LoadMetadata(path) is not { } asset)
        {
            return null;
        }

        if (kind is not null)
        {
            return OpenContributed(kind, path, asset.Id, readOnly);
        }

        if (Find(asset) is { } open)
        {
            Activate(open);
            return open;
        }

        return Open(asset, readOnly);
    }

    /// <summary>Opens a new empty scene backed by a temporary prefab for this editing session.</summary>
    /// <returns>The new scene document.</returns>
    public AssetWrapper NewScene() => Open(new TemporaryScene($"Untitled {++newSceneNumber}"));

    /// <summary>The open document with the given id, or null.</summary>
    /// <param name="id">A document id, which is its asset id.</param>
    public AssetWrapper? Find(string id) => documents.FirstOrDefault(document => document.Id == id);

    /// <summary>Brings an already-open document to the front.</summary>
    public void Activate(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Asset is not null)
        {
            assets.ActivateAsset(document.Asset);
        }
        else if (documents.Contains(document))
        {
            SetActive(document);
            DeactivateAssets();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Whether closing this document would lose edits, so a shell knows to ask before calling
    /// <see cref="Close"/>.
    /// </summary>
    public static bool NeedsSavePrompt(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IsDirty;
    }

    /// <summary>Closes a document, saving first when asked to.</summary>
    /// <param name="document">The document to close.</param>
    /// <param name="resolution">What to do with unsaved edits.</param>
    /// <returns>False when the requested save failed, which keeps the document open and dirty.</returns>
    public bool Close(AssetWrapper document, UnsavedChanges resolution = UnsavedChanges.Discard)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (resolution == UnsavedChanges.Save && !SaveBeforeClose(document))
        {
            return false;
        }

        if (document.Asset is not null)
        {
            assets.CloseAsset(document.Asset);
        }
        else
        {
            Remove(document);
        }

        return true;
    }

    /// <summary>Closes every document, discarding unsaved edits.</summary>
    public void CloseAll()
    {
        closingAll = true;
        try
        {
            foreach (var document in documents.ToList())
            {
                Close(document);
            }
        }
        finally
        {
            closingAll = false;
        }

        documents.Clear();
        ForgetWithdrawn();
        SetActive(null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Writes a document's edits to disk. A failure keeps it dirty. A refused save and a contributed document's
    /// failure are reported to the user; the asset writers report their own.
    /// </summary>
    /// <param name="document">The document to save.</param>
    /// <param name="overwrite">Whether to replace a source that conflicts with the edits.</param>
    /// <returns>Whether the source was written, and why not.</returns>
    public DocumentSaveResult Save(AssetWrapper document, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = document.Save(overwrite);
        if (result.Error is { } error && (document.Document is not null || document.IsReadOnly))
        {
            Report($"Could not save {document.Title}: {error}");
        }

        return result;
    }

    /// <summary>Writes every dirty document's edits to disk, continuing past one that fails.</summary>
    /// <returns>False when a document was not written or still has unsaved edits afterwards.</returns>
    public bool SaveAll()
    {
        var saved = true;
        foreach (var document in documents.Where(static document => document.IsDirty).ToList())
        {
            if (document is { Asset: { } asset, IsReadOnly: false })
            {
                assets.SaveAsset(asset);
            }
            else
            {
                saved &= Save(document).Succeeded;
            }
        }

        // A writer can report success and leave a document modified, so what is still dirty decides.
        return saved && !HasUnsavedChanges;
    }

    /// <summary>Drops a document's unsaved edits and reads its source again.</summary>
    /// <param name="document">The document to reload.</param>
    /// <returns>False when the source could not be read, which is reported to the user.</returns>
    public bool Reload(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.TryReload() is not { } error)
        {
            return true;
        }

        Report($"Could not reload {document.Title}: {error}");
        return false;
    }

    /// <summary>The open documents' asset ids, for restoring the session next time.</summary>
    public WorkspaceSession Capture() =>
        new(documents.Select(SessionId).OfType<Guid>().ToList(), SessionId(Active));

    /// <inheritdoc />
    public void Dispose()
    {
        CloseAll();
        kinds.Added -= OnKindAdded;
        kinds.Removed -= OnKindRemoved;
        assets.AssetOpened -= OnAssetOpened;
        assets.AssetClosed -= OnAssetClosed;
    }

    // A temporary scene has no source to come back to.
    static Guid? SessionId(AssetWrapper? document) =>
        document?.Asset is not TemporaryScene && Guid.TryParse(document?.Id, out var id) ? id : null;

    void OnAssetOpened(Asset? asset)
    {
        if (asset is null)
        {
            if (!deactivatingAssets)
            {
                SetActive(null);
                Changed?.Invoke();
            }

            return;
        }

        var document = Find(asset) ?? Track(asset);
        document.RefreshFromAsset();
        SetActive(document);
        Changed?.Invoke();
    }

    void OnAssetClosed(Asset asset)
    {
        if (Find(asset) is { } document && !ReferenceEquals(document, reloading))
        {
            Remove(document);
        }
    }

    AssetWrapper Track(Asset asset) =>
        Add(new AssetWrapper(asset, this,
            services is null
                ? new DocumentHistory()
                : new UndoServiceHistory(() => services.GetService<UndoService>(), asset.Id)));

    AssetWrapper Add(AssetWrapper document)
    {
        documents.Add(document);
        document.Changed += OnDocumentChanged;
        Opened?.Invoke(document);
        return document;
    }

    void OnDocumentChanged() => Changed?.Invoke();

    void Remove(AssetWrapper document)
    {
        if (!documents.Remove(document))
        {
            return;
        }

        Closed?.Invoke(document);
        document.Changed -= OnDocumentChanged;
        document.Dispose();
        Forget(document);
        if (document.Asset is TemporaryScene temporary)
        {
            temporary.Dispose();
        }

        ActivateAfterRemoving(document);
        Changed?.Invoke();
    }

    // The asset manager picks the next asset itself, but knows nothing of contributed documents.
    void ActivateAfterRemoving(AssetWrapper removed)
    {
        var wasActive = ReferenceEquals(Active, removed);
        if (closingAll)
        {
            if (wasActive)
            {
                SetActive(documents.LastOrDefault());
            }

            return;
        }

        var next = wasActive
            ? documents.LastOrDefault()
            : Active is null ? documents.LastOrDefault(static document => document.Asset is null) : null;
        if (next is not null)
        {
            Activate(next);
        }
        else if (wasActive)
        {
            SetActive(null);
        }
    }

    void SetActive(AssetWrapper? document)
    {
        if (ReferenceEquals(Active, document))
        {
            return;
        }

        Active = document;
        Activated?.Invoke(document);
    }

    AssetWrapper? Find(Asset asset) => Find(asset.Id.ToString());
}
