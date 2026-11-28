namespace Turian.Tests;

/// <summary>A contributed document kind for <c>.fake</c> sources, with switches for each way a brick can fail.</summary>
sealed class FakeDocumentKind : IWorkspaceDocumentKind
{
    public const string KindId = "test.fake";

    public List<FakeDocument> Opened { get; } = [];

    public Exception? CheckFailure { get; set; }

    public Exception? OpenFailure { get; set; }

    /// <summary>Makes <see cref="Open"/> break its contract by returning no document.</summary>
    public bool OpensNothing { get; set; }

    /// <summary>The file extension the kind claims.</summary>
    public string Extension { get; init; } = ".fake";

    /// <inheritdoc />
    public string Id { get; init; } = KindId;

    /// <inheritdoc />
    public string Title => "Fake";

    /// <inheritdoc />
    public bool CanOpen(string sourcePath) => CheckFailure is { } failure
        ? throw failure
        : sourcePath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IWorkspaceDocument Open(DocumentOpenContext context)
    {
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        if (OpensNothing)
        {
            return null!;
        }

        var document = new FakeDocument(context);
        Opened.Add(document);
        return document;
    }
}

/// <summary>An in-memory document whose content is a counter that each edit raises.</summary>
sealed class FakeDocument(DocumentOpenContext context) : IWorkspaceDocument
{
    public DocumentOpenContext Context => context;

    public int Value { get; private set; }

    /// <summary>The overwrite flag of every save the document was asked to make.</summary>
    public List<bool> Saves { get; } = [];

    public string? SaveFailure { get; set; }

    public Exception? SaveException { get; set; }

    public Exception? ReloadFailure { get; set; }

    public int Reloads { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? DisposeFailure { get; set; }

    public Exception? DirtyFailure { get; set; }

    /// <summary>Makes <see cref="Save"/> break its contract by returning no result.</summary>
    public bool SavesNothing { get; set; }

    /// <summary>Makes <see cref="Save"/> report success while leaving the edits unwritten.</summary>
    public bool StaysDirty { get; set; }

    bool dirty;

    /// <inheritdoc />
    public string Id => context.AssetId.ToString();

    /// <inheritdoc />
    public string Kind => FakeDocumentKind.KindId;

    /// <inheritdoc />
    public string Title => Path.GetFileNameWithoutExtension(context.SourcePath);

    /// <inheritdoc />
    public string? SourcePath => context.SourcePath;

    /// <inheritdoc />
    public bool IsDirty
    {
        get => DirtyFailure is { } failure ? throw failure : dirty;
        private set => dirty = value;
    }

    /// <inheritdoc />
    public bool IsReadOnly { get; set; }

    /// <inheritdoc />
    public string? Conflict { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    public void Edit(string label = "Edit Value")
    {
        Set(Value + 1);
        var after = Value;
        context.History.Record(label, () => Set(after - 1), () => Set(after));
    }

    public void ConflictWith(string reason)
    {
        Conflict = reason;
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public DocumentSaveResult Save(bool overwrite = false)
    {
        Saves.Add(overwrite);
        if (SaveException is { } exception)
        {
            throw exception;
        }

        if (SaveFailure is { } failure)
        {
            return DocumentSaveResult.Failed(failure);
        }

        if (SavesNothing)
        {
            return null!;
        }

        IsDirty = StaysDirty;
        Conflict = null;
        Changed?.Invoke();
        return DocumentSaveResult.Saved;
    }

    /// <inheritdoc />
    public void Reload()
    {
        if (ReloadFailure is { } failure)
        {
            throw failure;
        }

        Reloads++;
        Value = 0;
        IsDirty = false;
        Conflict = null;
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Disposed = true;
        if (DisposeFailure is { } failure)
        {
            throw failure;
        }
    }

    void Set(int value)
    {
        Value = value;
        IsDirty = true;
        Changed?.Invoke();
    }
}

/// <summary>A disposable project with a workspace that has the fake kind contributed.</summary>
sealed class DocumentProject : IDisposable
{
    readonly ServiceProvider services;

    /// <param name="liveKinds">
    /// Whether the workspace follows <see cref="Registry"/>, which starts empty, instead of always offering
    /// <see cref="Kind"/>.
    /// </param>
    public DocumentProject(bool liveKinds = false)
    {
        Settings.Set(new AppSettings { ProjectAbsoluteDir = Root });
        var collection = new ServiceCollection()
            .AddSingleton(Notifications)
            .AddSingleton(_ => Undo!);
        if (liveKinds)
        {
            collection.AddSingleton(Registry);
        }

        services = collection.BuildServiceProvider();
        Workspace = new AssetWorkspace(Assets, Settings, [Kind], services);
    }

    /// <summary>The live kinds, followed when the project was created with them.</summary>
    public WorkspaceDocumentKindRegistry Registry { get; } = new();

    public string Root { get; } = Directory.CreateTempSubdirectory("turian-documents-").FullName;

    public AssetManager Assets { get; } = new();

    public SettingsService Settings { get; } = new();

    public FakeDocumentKind Kind { get; } = new();

    public EditorNotifications Notifications { get; } = new();

    /// <summary>The undo service the asset tabs reach, set by a test that wires one before using it.</summary>
    public UndoService? Undo { get; set; }

    public AssetWorkspace Workspace { get; }

    /// <summary>Writes a source file and its asset metadata under the project's assets.</summary>
    public (string Path, Asset Asset) Source(string name)
    {
        var path = Path.Combine(Root, "Assets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);

        var asset = Path.GetExtension(name) switch
        {
            ".prefab" => new Prefab(),
            ".material" => new MaterialAsset(),
            _ => new Asset(),
        };
        asset.Id = Guid.NewGuid();
        asset.RelativePath = $"Assets/{name}";
        File.WriteAllText(path + ".meta", Serializer.Serialize(asset));
        return (path, asset);
    }

    /// <summary>Opens a new fake source as a document, returning its tab and the document behind it.</summary>
    public (AssetWrapper Tab, FakeDocument Document) OpenFake(string name, bool readOnly = false)
    {
        var tab = Workspace.Open(Source($"{name}.fake").Path, readOnly)!;
        return (tab, (FakeDocument)tab.Document!);
    }

    /// <summary>The messages posted to the user since the last call.</summary>
    public List<string> Reported()
    {
        var messages = new List<string>();
        while (Notifications.TryTake(out var notification))
        {
            Assert.True(notification.IsError);
            messages.Add(notification.Message);
        }

        return messages;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Workspace.Dispose();
        services.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
