namespace Turian.Tests;

/// <summary>What open documents do when editor bricks add and remove their kinds.</summary>
public sealed class WorkspaceDocumentKindTests : IDisposable
{
    readonly DocumentProject project = new(liveKinds: true);

    AssetWorkspace Workspace => project.Workspace;

    /// <inheritdoc />
    public void Dispose() => project.Dispose();

    static FakeDocument DocumentOf(AssetWrapper tab) => (FakeDocument)tab.Document!;

    /// <summary>The registry offers a kind only while its registration lives, and reports each change once.</summary>
    [Fact]
    public void TheRegistryOffersAKindWhileItsRegistrationLives()
    {
        var seeded = new FakeDocumentKind { Id = "seeded" };
        var registry = new WorkspaceDocumentKindRegistry([seeded]);
        var events = new List<string>();
        registry.Added += kind => events.Add($"+{kind.Id}");
        registry.Removed += kind => events.Add($"-{kind.Id}");

        var registration = registry.Add(project.Kind);

        Assert.Equal([seeded, project.Kind], registry.Kinds);
        registration.Dispose();
        registration.Dispose();
        Assert.Equal([seeded], registry.Kinds);
        Assert.Equal([$"+{FakeDocumentKind.KindId}", $"-{FakeDocumentKind.KindId}"], events);
    }

    /// <summary>A kind added after the workspace was built claims sources, and stops when it is removed.</summary>
    [Fact]
    public void AnAddedKindClaimsSourcesUntilItIsRemoved()
    {
        var (path, _) = project.Source("menu.fake");
        Assert.Null(Workspace.KindFor(path));

        var registration = project.Registry.Add(project.Kind);

        Assert.Same(project.Kind, Workspace.KindFor(path));
        registration.Dispose();
        Assert.Null(Workspace.KindFor(path));
    }

    /// <summary>Clean documents close with their kind and open again, as they were, with the returning one.</summary>
    [Fact]
    public void CleanDocumentsCloseAndReopenWithTheReturningKind()
    {
        var registration = project.Registry.Add(project.Kind);
        var scene = Workspace.Open(project.Source("level.prefab").Path)!;
        var first = Workspace.Open(project.Source("first.fake").Path, readOnly: true)!;
        var (_, second) = project.OpenFake("second");
        var old = project.Kind.Opened.ToArray();

        registration.Dispose();

        Assert.Equal([scene], Workspace.Documents);
        Assert.Same(scene, Workspace.Active);
        Assert.All(old, static document => Assert.True(document.Disposed));

        var returning = new FakeDocumentKind();
        project.Registry.Add(returning);

        Assert.Equal(3, Workspace.Documents.Count);
        Assert.Equal(2, returning.Opened.Count);
        Assert.Equal(second.Id, Workspace.Active!.Id);
        Assert.True(Workspace.Find(first.Id)!.IsReadOnly);
        Assert.False(Workspace.Find(second.Id)!.IsReadOnly);
        Assert.Empty(project.Reported());
    }

    /// <summary>A tab the user picked while a kind was away stays in front when the kind returns.</summary>
    [Fact]
    public void AReturningKindDoesNotStealTheTabTheUserPicked()
    {
        var registration = project.Registry.Add(project.Kind);
        var scene = Workspace.Open(project.Source("level.prefab").Path)!;
        var other = Workspace.Open(project.Source("other.prefab").Path)!;
        var (menu, _) = project.OpenFake("menu");
        registration.Dispose();
        Assert.Same(other, Workspace.Active);

        Workspace.Activate(scene);
        project.Registry.Add(new FakeDocumentKind());

        Assert.NotNull(Workspace.Find(menu.Id));
        Assert.Same(scene, Workspace.Active);
    }

    /// <summary>When every tab was of the removed kind, the one that was in front returns in front.</summary>
    [Fact]
    public void TheActiveDocumentReturnsInFrontWhenNothingElseWasOpen()
    {
        var registration = project.Registry.Add(project.Kind);
        var (first, _) = project.OpenFake("first");
        var (second, _) = project.OpenFake("second");
        Workspace.Activate(first);

        registration.Dispose();
        Assert.Empty(Workspace.Documents);
        Assert.Null(Workspace.Active);
        project.Registry.Add(new FakeDocumentKind());

        Assert.Equal([first.Id, second.Id], Workspace.Documents.Select(static tab => tab.Id));
        Assert.Equal(first.Id, Workspace.Active!.Id);
    }

    /// <summary>Removing two kinds in turn still brings the originally active document back to the front.</summary>
    [Fact]
    public void RemovingSeveralKindsRemembersTheFirstActiveDocument()
    {
        var other = new FakeDocumentKind { Id = "test.other", Extension = ".other" };
        var otherRegistration = project.Registry.Add(other);
        var registration = project.Registry.Add(project.Kind);
        var (otherPath, otherAsset) = project.Source("tool.other");
        Workspace.Open(otherPath);
        var (menu, _) = project.OpenFake("menu");

        registration.Dispose();
        Assert.Equal(otherAsset.Id.ToString(), Workspace.Active!.Id);
        otherRegistration.Dispose();
        project.Registry.Add(new FakeDocumentKind { Id = "test.other", Extension = ".other" });
        project.Registry.Add(new FakeDocumentKind());

        Assert.Equal(menu.Id, Workspace.Active!.Id);
        Assert.Equal(2, Workspace.Documents.Count);
    }

    /// <summary>A dirty document outlives its kind: reported, still usable and still the tab of its source.</summary>
    [Fact]
    public void ADirtyDocumentStaysOnTheOldInstanceAndCanStillBeSaved()
    {
        var registration = project.Registry.Add(project.Kind);
        var (path, asset) = project.Source("menu.fake");
        var tab = Workspace.Open(path)!;
        var document = DocumentOf(tab);
        document.Edit();
        var scene = Workspace.Open(project.Source("level.prefab").Path)!;

        registration.Dispose();

        Assert.Contains(tab, Workspace.Documents);
        Assert.False(document.Disposed);
        Assert.Empty(document.Saves);
        Assert.Contains("unsaved changes", Assert.Single(project.Reported()));
        Assert.Same(tab, Workspace.Open(path));
        Assert.Same(tab, Workspace.Active);
        Workspace.Activate(scene);
        Assert.Same(tab, Workspace.Open(asset));
        Assert.Null(project.Assets.ActiveAsset);

        var returning = new FakeDocumentKind();
        project.Registry.Add(returning);
        Assert.Empty(returning.Opened);

        Assert.True(Workspace.Save(tab).Succeeded);
        Assert.False(tab.IsDirty);
        Assert.True(Workspace.Close(tab));
        Assert.True(document.Disposed);
    }

    /// <summary>A dirty tab saved and closed under its old instance reopens with the kind that came back.</summary>
    [Fact]
    public void ASavedOldInstanceReopensWithTheReturningKind()
    {
        var registration = project.Registry.Add(project.Kind);
        var (path, _) = project.Source("menu.fake");
        var tab = Workspace.Open(path)!;
        DocumentOf(tab).Edit();
        registration.Dispose();
        Assert.Single(project.Reported());
        Assert.True(Workspace.Save(tab).Succeeded);
        Assert.True(Workspace.Close(tab));

        var returning = new FakeDocumentKind();
        project.Registry.Add(returning);
        var reopened = Workspace.Open(path)!;

        Assert.Same(returning.Opened.Single(), reopened.Document);
        Assert.Single(Workspace.Documents);
        Assert.Empty(project.Reported());
    }

    /// <summary>When a kind is removed, its clean tabs are already closed by the time the removal is announced.</summary>
    [Fact]
    public void TabsAreClosedBeforeTheRemovalIsAnnounced()
    {
        var registration = project.Registry.Add(project.Kind);
        project.OpenFake("menu");
        var fakeTabsAtAnnouncement = new List<int>();
        project.Registry.Removed += _ =>
            fakeTabsAtAnnouncement.Add(Workspace.Documents.Count(static tab => tab.Document is FakeDocument));

        registration.Dispose();

        Assert.Equal([0], fakeTabsAtAnnouncement);
    }

    /// <summary>Documents are not brought back after every tab was closed, as on a project switch.</summary>
    [Fact]
    public void ClosingEverythingForgetsWithdrawnDocuments()
    {
        var registration = project.Registry.Add(project.Kind);
        project.OpenFake("menu");
        registration.Dispose();

        Workspace.CloseAll();
        project.Registry.Add(new FakeDocumentKind());

        Assert.Empty(Workspace.Documents);
    }

    /// <summary>A deleted source, or one the returning kind no longer claims, is not reopened.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASourceThatCannotComeBackIsSkipped(bool deleted)
    {
        var registration = project.Registry.Add(project.Kind);
        var (path, _) = project.Source("menu.fake");
        Workspace.Open(path);
        registration.Dispose();
        if (deleted)
        {
            File.Delete(path);
        }

        var returning = new FakeDocumentKind { Extension = deleted ? ".fake" : ".changed" };
        project.Registry.Add(returning);

        Assert.Empty(Workspace.Documents);
        Assert.Empty(returning.Opened);
    }

    /// <summary>A source opened as an asset in the meantime keeps that tab.</summary>
    [Fact]
    public void ASourceReopenedAsAnAssetKeepsItsTab()
    {
        var registration = project.Registry.Add(project.Kind);
        var (path, asset) = project.Source("menu.fake");
        Workspace.Open(path);
        registration.Dispose();
        var plain = Workspace.Open(asset);

        var returning = new FakeDocumentKind();
        project.Registry.Add(returning);

        Assert.Equal([plain], Workspace.Documents);
        Assert.Empty(returning.Opened);
    }

    /// <summary>A returning kind that cannot open the source reports it.</summary>
    [Fact]
    public void AReturningKindThatFailsToOpenReportsIt()
    {
        var registration = project.Registry.Add(project.Kind);
        project.OpenFake("menu");
        registration.Dispose();

        project.Registry.Add(new FakeDocumentKind { OpenFailure = new FormatException("corrupt") });

        Assert.Empty(Workspace.Documents);
        Assert.Contains("corrupt", Assert.Single(project.Reported()));
    }

    /// <summary>A disposed workspace no longer follows the registry.</summary>
    [Fact]
    public void ADisposedWorkspaceStopsFollowingTheRegistry()
    {
        var registration = project.Registry.Add(project.Kind);
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        Workspace.Dispose();

        registration.Dispose();

        Assert.DoesNotContain(tab, Workspace.Documents);
        Assert.Empty(project.Reported());
    }
}
