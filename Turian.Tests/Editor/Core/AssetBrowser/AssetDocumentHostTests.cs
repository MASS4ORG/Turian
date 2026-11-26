namespace Turian.Tests;

/// <summary>Covers how Save, Save All, close prompts and Undo/Redo reach document editors with their own state.</summary>
public sealed class AssetDocumentHostTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-documents-{Guid.NewGuid():N}");
    readonly BuildManager build = new(new BuildAppSettings(), NullLogger.Instance);
    readonly UserPanelCatalog catalog;
    readonly AssetManager assets = new();
    readonly EditorNotifications notifications = new();
    readonly SettingsService settings = new();
    readonly AssetDocumentHost host;

    /// <summary>A document editor that records what it is asked and can fail to write.</summary>
    [Panel("Document Fixture", "Tools/Document Fixture")]
    public sealed class DocumentFixture : IAssetDocumentEditor, IAssetDocumentSession, IDisposable
    {
        /// <summary>The undo steps left to revert.</summary>
        public int Undos { get; set; }

        /// <summary>The redo steps left to reapply.</summary>
        public int Redos { get; set; }

        /// <summary>How often the editor reverted an edit.</summary>
        public int UndoCalls { get; private set; }

        /// <summary>How often the editor reapplied an edit.</summary>
        public int RedoCalls { get; private set; }

        /// <summary>How often the document was written.</summary>
        public int Saves { get; private set; }

        /// <summary>How often the document was dropped.</summary>
        public int Discards { get; private set; }

        /// <summary>How often the editor was released.</summary>
        public int Releases { get; private set; }

        /// <summary>The reason the next write fails, or null for success.</summary>
        public string? Failure { get; set; }

        /// <summary>Whether the next write throws instead of reporting.</summary>
        public bool Throws { get; set; }

        /// <inheritdoc />
        public string? DocumentPath { get; set; }

        /// <inheritdoc />
        public bool IsDirty { get; set; }


        /// <inheritdoc />
        public bool CanUndo => Undos > 0;

        /// <inheritdoc />
        public bool CanRedo => Redos > 0;

        /// <inheritdoc />
        public bool CanOpen(string sourcePath) =>
            sourcePath.EndsWith(".ui", StringComparison.Ordinal) || sourcePath.EndsWith(".pss", StringComparison.Ordinal);

        /// <inheritdoc />
        public void Open(string sourcePath, bool isReadOnly = false) => DocumentPath = sourcePath;

        /// <inheritdoc />
        public bool TrySave(out string? error)
        {
            if (Throws)
            {
                throw new IOException("disk full");
            }

            error = Failure;
            if (Failure is not null)
            {
                return false;
            }

            Saves++;
            IsDirty = false;
            return true;
        }

        /// <inheritdoc />
        public void Discard()
        {
            Discards++;
            IsDirty = false;
            DocumentPath = null;
        }

        /// <inheritdoc />
        public void Undo()
        {
            UndoCalls++;
            Undos--;
            Redos++;
        }

        /// <inheritdoc />
        public void Redo()
        {
            RedoCalls++;
            Redos--;
            Undos++;
        }

        /// <inheritdoc />
        public void Dispose() => Releases++;
    }

    /// <summary>Creates the host over an empty catalog.</summary>
    public AssetDocumentHostTests()
    {
        Directory.CreateDirectory(project);
        settings.Set(new AppSettings { ProjectAbsoluteDir = project });
        catalog = new UserPanelCatalog(build, NullLogger.Instance);
        host = new AssetDocumentHost(catalog, settings, assets, NullLogger.Instance, notifications);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        host.Dispose();
        catalog.Dispose();
        build.Dispose();
        Directory.Delete(project, recursive: true);
    }

    DocumentFixture Add(string name = "Menu.ui", bool dirty = false, bool focus = false)
    {
        var fixture = new DocumentFixture
        {
            DocumentPath = Path.Combine(project, "Assets", name),
            IsDirty = dirty,
        };
        ((IList<UserPanelPage>)catalog.Pages).Add(new UserPanelPage($"doc.{name}", name, $"Tools/{name}", fixture));
        if (focus)
        {
            host.FocusedPanel = () => $"doc.{name}";
        }

        return fixture;
    }

    /// <summary>Save writes the focused editor's document; without focus the scene tabs keep Save.</summary>
    [Fact]
    public void SaveWritesOnlyTheFocusedDocument()
    {
        var other = Add("Other.ui", dirty: true);
        Assert.Null(host.SaveActive());

        var focused = Add(dirty: true, focus: true);

        Assert.True(host.SaveActive());
        Assert.Equal(1, focused.Saves);
        Assert.False(focused.IsDirty);
        Assert.Equal(0, other.Saves);
    }

    /// <summary>A failed write leaves the document dirty and tells the user why.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSaveStaysDirtyAndIsReported(bool throws)
    {
        var failing = Add(dirty: true, focus: true);
        failing.Failure = throws ? null : "read-only";
        failing.Throws = throws;

        Assert.False(host.SaveActive());

        Assert.True(failing.IsDirty);
        Assert.True(host.HasUnsavedChanges);
        Assert.True(notifications.TryTake(out var report));
        Assert.True(report.IsError);
        Assert.Contains("Menu.ui", report.Message, StringComparison.Ordinal);
        Assert.Contains(throws ? "disk full" : "read-only", report.Message, StringComparison.Ordinal);
    }

    /// <summary>Save All writes every dirty editor, continues past a failure, and reports it.</summary>
    [Fact]
    public void SaveAllIncludesDirtyEditorsAndContinuesPastFailure()
    {
        var failing = Add("A.ui", dirty: true);
        failing.Failure = "locked";
        var good = Add("B.ui", dirty: true);
        var clean = Add("C.ui");

        Assert.False(host.SaveAll());

        Assert.Equal(1, good.Saves);
        Assert.Equal(0, clean.Saves);
        Assert.True(failing.IsDirty);
        Assert.False(good.IsDirty);

        failing.Failure = null;
        Assert.True(host.SaveAll());
        Assert.False(host.HasUnsavedChanges);
    }

    /// <summary>Closing a dirty document applies the choice: save writes first, discard drops, a failed save keeps it open.</summary>
    [Fact]
    public void ResolveAppliesTheUnsavedChoice()
    {
        var saved = Add("A.ui", dirty: true);
        var discarded = Add("B.ui", dirty: true);
        var failing = Add("C.ui", dirty: true);
        failing.Failure = "locked";

        Assert.True(host.Resolve(saved, UnsavedChanges.Save));
        Assert.True(host.Resolve(discarded, UnsavedChanges.Discard));
        Assert.False(host.Resolve(failing, UnsavedChanges.Save));

        Assert.Equal(1, saved.Saves);
        Assert.Equal(1, saved.Discards);
        Assert.Equal(0, discarded.Saves);
        Assert.Equal(1, discarded.Discards);
        Assert.Equal(0, failing.Discards);
        Assert.True(failing.IsDirty);
    }

    /// <summary>The close prompt offers save, discard and cancel; only cancel keeps the document untouched.</summary>
    [Fact]
    public void RequestCloseFollowsThePromptOutcome()
    {
        var session = Add(dirty: true);
        Action<UnsavedChanges>? answer = null;
        Action? cancel = null;
        host.Prompt = (_, decided, cancelled) => (answer, cancel) = (decided, cancelled);
        var closed = 0;
        var kept = 0;

        host.RequestClose(session, () => closed++, () => kept++);
        Assert.NotNull(answer);
        cancel!();
        Assert.Equal((0, 1), (closed, kept));
        Assert.True(session.IsDirty);

        host.RequestClose(session, () => closed++);
        Assert.Equal(0, closed);
        Assert.True(session.IsDirty);
        Assert.Equal(0, session.Discards);

        answer = null;
        host.RequestClose(session, () => closed++);
        answer!(UnsavedChanges.Save);
        Assert.Equal(1, closed);
        Assert.Equal(1, session.Saves);

        session.IsDirty = true;
        session.DocumentPath = "x.ui";
        host.RequestClose(session, () => closed++);
        answer!(UnsavedChanges.Discard);
        Assert.Equal(2, closed);
        Assert.Equal(1, session.Saves);
    }

    /// <summary>A save that fails during the prompt keeps the document open.</summary>
    [Fact]
    public void RequestCloseStaysOpenWhenSaveFails()
    {
        var session = Add(dirty: true);
        session.Failure = "locked";
        host.Prompt = static (_, decided, _) => decided(UnsavedChanges.Save);
        var closed = 0;
        var kept = 0;

        host.RequestClose(session, () => closed++, () => kept++);

        Assert.Equal((0, 1), (closed, kept));
        Assert.True(session.IsDirty);
    }

    /// <summary>A clean document, or a shell without a prompt, closes at once.</summary>
    [Fact]
    public void RequestCloseSkipsThePromptWhenNothingIsAtStake()
    {
        var clean = Add("A.ui");
        var dirty = Add("B.ui", dirty: true);
        var asked = 0;
        host.Prompt = (_, _, _) => asked++;
        var closed = 0;

        host.RequestClose(clean, () => closed++);
        Assert.Equal((0, 1), (asked, closed));

        host.Prompt = null;
        host.RequestClose(dirty);
        Assert.Equal(1, dirty.Discards);
    }

    /// <summary>Undo and Redo go to the focused editor's own stack and leave the scene stack alone.</summary>
    [Fact]
    public void UndoRoutesByFocus()
    {
        var unfocused = Add("A.ui");
        unfocused.Undos = 1;
        Assert.False(host.Undo());
        Assert.False(host.Redo());
        Assert.False(host.CanUndo);
        host.FocusedPanel = static () => "doc.A.ui";
        Assert.True(host.CanUndo);
        host.FocusedPanel = static () => "scene.tree";
        Assert.False(host.CanUndo);

        var focused = Add("B.ui", focus: true);
        focused.Undos = 1;

        Assert.True(host.CanUndo);
        Assert.False(host.CanRedo);
        Assert.True(host.Undo());
        Assert.True(host.CanRedo);
        Assert.True(host.Redo());
        Assert.Equal((1, 1), (focused.UndoCalls, focused.RedoCalls));
        Assert.Equal(0, unfocused.UndoCalls);

        // An empty stack still takes the command, so the scene's history is not undone behind the user's back.
        focused.Undos = 0;
        Assert.True(host.Undo());
        Assert.Equal(1, focused.UndoCalls);
    }

    /// <summary>Leaving the project is blocked while a document cannot be written, and counts dirty editors.</summary>
    [Fact]
    public void UnsavedWorkCoversDocumentEditors()
    {
        var session = Add(dirty: true);
        using var workspace = new AssetWorkspace(assets, settings);
        var unsaved = new UnsavedWork(workspace, new AssetAutoSave(
            new AssetInspectionService(null!, new AssetTypeCatalog(), settings, null!, NullLogger.Instance)), host);
        Assert.True(unsaved.Any);

        session.Failure = "locked";
        Assert.False(unsaved.SaveAll());
        Assert.True(unsaved.Any);

        session.Failure = null;
        Assert.True(unsaved.SaveAll());
        Assert.False(unsaved.Any);

        session.IsDirty = true;
        unsaved.DiscardAll();
        Assert.Equal(1, session.Discards);
        Assert.False(unsaved.Any);
    }

    /// <summary>An asset whose source an editor owns is clean only after the editor wrote it; a failure keeps it dirty.</summary>
    [Fact]
    public void AssetManagerKeepsEditorOwnedSourceDirtyUntilWritten()
    {
        var session = Add(dirty: true);
        var asset = new Asset { Id = Guid.NewGuid(), RelativePath = Path.Combine("Assets", "Menu.ui") };
        assets.OpenAsset(asset);
        assets.AlterAsset(asset);
        session.Failure = "locked";

        Assert.False(assets.SaveAsset(asset));
        Assert.True(asset.IsModified);
        assets.SaveAllAssets();
        Assert.True(asset.IsModified);
        Assert.True(session.IsDirty);

        session.Failure = null;
        Assert.True(assets.SaveAsset(asset));
        Assert.False(asset.IsModified);
        Assert.Equal(1, session.Saves);
    }

    /// <summary>An asset no editor owns is saved as before.</summary>
    [Fact]
    public void AssetManagerSavesUnownedAssetsAsBefore()
    {
        var asset = new Asset { Id = Guid.NewGuid(), RelativePath = Path.Combine("Assets", "Other.ui") };
        assets.OpenAsset(asset);
        assets.AlterAsset(asset);

        Assert.True(assets.SaveAsset(asset));
        Assert.False(asset.IsModified);
    }

    /// <summary>Closing a workspace tab with Save keeps it open when the write failed.</summary>
    [Fact]
    public void WorkspaceKeepsDocumentOpenWhenSaveOnCloseFails()
    {
        var session = Add(dirty: true);
        session.Failure = "locked";
        using var workspace = new AssetWorkspace(assets, settings);
        var asset = new Asset { Id = Guid.NewGuid(), RelativePath = Path.Combine("Assets", "Menu.ui") };
        var document = workspace.Open(asset);
        assets.AlterAsset(asset);

        workspace.Close(document, UnsavedChanges.Save);
        Assert.Contains(document, workspace.Documents);
        Assert.True(asset.IsModified);

        session.Failure = null;
        workspace.Close(document, UnsavedChanges.Save);
        Assert.DoesNotContain(document, workspace.Documents);
    }

    /// <summary>Switching project releases the old editors and a new project's catalog never sees their documents.</summary>
    [Fact]
    public void ProjectSwitchReleasesAndIsolatesDocumentEditors()
    {
        using var otherBuild = new BuildManager(new BuildAppSettings(), NullLogger.Instance);
        using var other = new UserPanelCatalog(otherBuild, NullLogger.Instance);

        var assembly = typeof(DocumentFixture).Assembly;
        var first = (DocumentFixture)catalog.Refresh([assembly]).Single(static page => page.Path == "Tools/Document Fixture")
            .Target;
        var second = (DocumentFixture)other.Refresh([assembly]).Single(static page => page.Path == "Tools/Document Fixture")
            .Target;
        first.Open("first.ui");
        first.IsDirty = true;

        Assert.NotSame(first, second);
        Assert.Null(second.DocumentPath);

        catalog.Refresh([]);

        Assert.Equal(1, first.Releases);
        Assert.Equal(0, second.Releases);
        Assert.Empty(host.Sessions);
        Assert.False(host.HasUnsavedChanges);
    }
}
