namespace Turian.Editor.Core;

/// <summary>
/// The set of assets currently open for editing, in tab order, with one of them active. UI-agnostic:
/// a shell renders <see cref="Documents"/> and reacts to the events; every decision that is policy
/// rather than presentation — what closing a dirty document means, whether play mode is available —
/// lives here.
/// </summary>
public sealed class AssetWorkspace : IDisposable
{
    readonly AssetManager assets;
    readonly List<AssetWrapper> documents = [];

    /// <summary>Creates a workspace over the asset manager, resetting when a project is loaded.</summary>
    /// <param name="assets">The asset manager this workspace drives.</param>
    /// <param name="settings">Watched so opening a project clears the previous one's documents.</param>
    public AssetWorkspace(AssetManager assets, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(settings);

        this.assets = assets;

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
    public bool HasUnsavedChanges => documents.Any(document => document.IsDirty);

    /// <summary>
    /// Whether a scene is open, which is what play mode needs. A shell binds its Play button to this.
    /// </summary>
    public bool HasOpenScene => documents.Any(document => document.Asset is Prefab);

    /// <summary>Raised when a document joins the workspace.</summary>
    public event Action<AssetWrapper>? Opened;

    /// <summary>Raised when a document leaves the workspace.</summary>
    public event Action<AssetWrapper>? Closed;

    /// <summary>Raised when the active document changes, with null when nothing is left open.</summary>
    public event Action<AssetWrapper?>? Activated;

    /// <summary>
    /// Raised whenever anything a tab strip draws may have changed — the document list, the active
    /// one, or a dirty marker.
    /// </summary>
    public event Action? Changed;

    /// <summary>Opens an asset, or activates it when it is already open.</summary>
    /// <param name="asset">The asset to open.</param>
    /// <returns>The document for it.</returns>
    public AssetWrapper Open(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        assets.OpenAsset(asset);
        return Find(asset) ?? Track(asset);
    }

    /// <summary>Brings an already-open document to the front.</summary>
    public void Activate(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Asset is not null) assets.ActivateAsset(document.Asset);
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
    public void Close(AssetWrapper document, UnsavedChanges resolution = UnsavedChanges.Discard)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (resolution == UnsavedChanges.Save && document.Asset is not null)
            assets.SaveAsset(document.Asset);

        if (document.Asset is not null) assets.CloseAsset(document.Asset);
        else Remove(document);
    }

    /// <summary>Closes every document, discarding unsaved edits.</summary>
    public void CloseAll()
    {
        foreach (var document in documents.ToList()) Close(document);

        documents.Clear();
        SetActive(null);
        Changed?.Invoke();
    }

    /// <summary>Writes a document's edits to disk.</summary>
    public void Save(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Asset is not null) assets.SaveAsset(document.Asset);
    }

    /// <summary>Writes every open document's edits to disk.</summary>
    public void SaveAll() => assets.SaveAllAssets();

    /// <summary>The open documents' asset ids, for restoring the session next time.</summary>
    public WorkspaceSession Capture() =>
        new(documents.Where(d => d.Asset is not null).Select(d => d.Asset!.Id).ToList(),
            Active?.Asset?.Id);

    /// <inheritdoc />
    public void Dispose()
    {
        assets.AssetOpened -= OnAssetOpened;
        assets.AssetClosed -= OnAssetClosed;

        foreach (var document in documents) document.Dispose();
        documents.Clear();
    }

    void OnAssetOpened(Asset? asset)
    {
        if (asset is null)
        {
            SetActive(null);
            Changed?.Invoke();
            return;
        }

        var document = Find(asset) ?? Track(asset);
        document.RefreshFromAsset();
        SetActive(document);
        Changed?.Invoke();
    }

    void OnAssetClosed(Asset asset)
    {
        if (Find(asset) is { } document) Remove(document);
    }

    AssetWrapper Track(Asset asset)
    {
        var document = new AssetWrapper(asset);
        documents.Add(document);
        Opened?.Invoke(document);
        return document;
    }

    void Remove(AssetWrapper document)
    {
        if (!documents.Remove(document)) return;

        Closed?.Invoke(document);
        document.Dispose();

        if (ReferenceEquals(Active, document)) SetActive(documents.LastOrDefault());

        Changed?.Invoke();
    }

    void SetActive(AssetWrapper? document)
    {
        if (ReferenceEquals(Active, document)) return;

        Active = document;
        Activated?.Invoke(document);
    }

    AssetWrapper? Find(Asset asset) =>
        documents.FirstOrDefault(document => document.Asset?.Id == asset.Id);
}
