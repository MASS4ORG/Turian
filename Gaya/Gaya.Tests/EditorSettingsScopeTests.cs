namespace Gaya.Tests;

/// <summary>Tests for the layered settings scopes: Studio, User, Workspace and Personal.</summary>
public class EditorSettingsScopeTests : IDisposable
{
    const string PageId = "test.scopes";

    static readonly string ProjectId = Guid.NewGuid().ToString("D");

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-settings-scopes-{Guid.NewGuid():N}");

    string StudioFile => Path.Combine(root, "studio.json");

    string UserFile => Path.Combine(root, "settings.json");

    string ProjectDirectory => Path.Combine(root, "project");

    string WorkspaceFile => Path.Combine(ProjectDirectory, ".gaya", "settings.json");

    string PersonalFile => Path.Combine(root, "projects", ProjectId, "settings.json");

    [EditorSetting("Editor", Id = PageId, Scope = SettingsScope.User,
        Scopes = SettingsScope.Studio | SettingsScope.Workspace | SettingsScope.Personal)]
    sealed class LayeredPage
    {
        public string Editor { get; [UsedImplicitly] set; } = "builtin";

        public int Port { get; [UsedImplicitly] set; } = 1;
    }

    [EditorSetting("Single", Id = "test.single")]
    sealed class SinglePage
    {
        public int Value { get; [UsedImplicitly] set; }
    }

    /// <summary>Deletes everything written under the temporary root.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    EditorSettings Create(bool bind = true)
    {
        var settings = new EditorSettings(NullLogger.Instance, UserFile, StudioFile, Path.Combine(root, "projects"));
        if (bind)
        {
            settings.BindWorkspace(ProjectDirectory);
            settings.BindPersonal(ProjectId);
        }

        return settings;
    }

    void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>A settings file holding one page whose members are the given JSON object.</summary>
    static string Page(string members) => $$"""{"{{PageId}}":""" + members + "}";

    JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    /// <summary>The highest scope holding a member wins, and each lower scope is what shows when it is cleared.</summary>
    [Fact]
    public void HigherScopesOverrideLowerOnes()
    {
        Write(StudioFile, Page("""{"Editor":"studio"}"""));
        Write(UserFile, Page("""{"Editor":"user"}"""));
        Write(WorkspaceFile, Page("""{"Editor":"workspace"}"""));
        Write(PersonalFile, Page("""{"Editor":"personal"}"""));
        var settings = Create();
        var page = new LayeredPage();

        settings.Register(page);

        Assert.Equal("personal", page.Editor);
        Assert.Equal(SettingsScope.Personal, settings.EffectiveScope(PageId, "Editor"));
        Assert.Equal([SettingsScope.Studio, SettingsScope.User, SettingsScope.Workspace, SettingsScope.Personal],
            settings.ScopesHolding(PageId, "Editor"));

        Assert.True(settings.Reset(PageId, "Editor", SettingsScope.Personal).Succeeded);
        Assert.Equal("workspace", page.Editor);
        settings.Reset(PageId, "Editor", SettingsScope.Workspace);
        Assert.Equal("user", page.Editor);
        settings.Reset(PageId, "Editor", SettingsScope.User);
        Assert.Equal("studio", page.Editor);
        var last = settings.Reset(PageId, "Editor", SettingsScope.Studio);
        Assert.Equal("builtin", page.Editor);
        Assert.Null(last.Effective);
    }

    /// <summary>A scope that names only some members leaves the rest to the scopes below.</summary>
    [Fact]
    public void MembersResolveIndependently()
    {
        Write(UserFile, Page("""{"Editor":"user","Port":7}"""));
        Write(PersonalFile, Page("""{"Editor":"personal"}"""));
        var page = new LayeredPage();

        Create().Register(page);

        Assert.Equal("personal", page.Editor);
        Assert.Equal(7, page.Port);
    }

    /// <summary>A missing Studio, Workspace or Personal file falls through without error.</summary>
    [Fact]
    public void MissingScopesFallBack()
    {
        Write(UserFile, Page("""{"Port":3}"""));
        var page = new LayeredPage();

        Create(bind: false).Register(page);

        Assert.Equal("builtin", page.Editor);
        Assert.Equal(3, page.Port);
    }

    /// <summary>An edit lands in the scope that holds the value, leaving every other scope untouched.</summary>
    [Fact]
    public void EditsStayInTheScopeThatHoldsTheValue()
    {
        Write(WorkspaceFile, Page("""{"Editor":"workspace"}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        page.Editor = "edited";
        settings.NotifyChanged(PageId);
        settings.Save();

        Assert.Equal("edited", Read(WorkspaceFile)[PageId]!["Editor"]!.GetValue<string>());
        Assert.False(File.Exists(PersonalFile));
    }

    /// <summary>A value no scope holds is first written to the page's home scope, and only that member.</summary>
    [Fact]
    public void FirstEditGoesToTheHomeScopeAndStaysSparse()
    {
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        page.Port = 9;
        settings.NotifyChanged(PageId);
        settings.Save();

        var stored = Read(UserFile)[PageId]!.AsObject();
        Assert.Equal(9, stored["Port"]!.GetValue<int>());
        Assert.False(stored.ContainsKey("Editor"));
        Assert.False(File.Exists(PersonalFile));
        Assert.False(File.Exists(StudioFile));
    }

    /// <summary>Writing to an explicit scope overrides the lower ones and reports where the value comes from.</summary>
    [Fact]
    public void ExplicitWriteOverridesLowerScopes()
    {
        Write(UserFile, Page("""{"Editor":"user"}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        var change = settings.Write(PageId, "Editor", SettingsScope.Personal, "mine");

        Assert.True(change.Succeeded);
        Assert.Equal(SettingsScope.Personal, change.Effective);
        Assert.Equal("mine", page.Editor);
        Assert.True(settings.TryRead(PageId, "Editor", SettingsScope.User, out var lower));
        Assert.Equal("user", lower);
    }

    /// <summary>A move leaves the value in the destination only, and says so.</summary>
    [Fact]
    public void MoveLeavesTheValueInTheDestinationOnly()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared","Port":4}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        var change = settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal);

        Assert.True(change.Succeeded);
        Assert.Equal(SettingsScope.Personal, change.Effective);
        Assert.False(Read(WorkspaceFile)[PageId]!.AsObject().ContainsKey("Editor"));
        Assert.Equal(4, Read(WorkspaceFile)[PageId]!["Port"]!.GetValue<int>());
        Assert.Equal("shared", Read(PersonalFile)[PageId]!["Editor"]!.GetValue<string>());
        Assert.Equal([SettingsScope.Personal], settings.ScopesHolding(PageId, "Editor"));
    }

    /// <summary>A move whose destination cannot be written leaves the source holding the value.</summary>
    [Fact]
    public void FailedMoveKeepsTheSource()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);
        settings.BindPersonal(null);

        var change = settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal);

        Assert.False(change.Succeeded);
        Assert.Equal(SettingsScope.Workspace, change.Effective);
        Assert.Equal("shared", Read(WorkspaceFile)[PageId]!["Editor"]!.GetValue<string>());
    }

    /// <summary>A destination that cannot be written to is never left holding half a move.</summary>
    [Fact]
    public void MoveToAnUnwritableFileLeavesBothScopesAsTheyWere()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        Write(PersonalFile, "{broken");
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        var change = settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal);

        Assert.False(change.Succeeded);
        Assert.Equal("{broken", File.ReadAllText(PersonalFile));
        Assert.Equal("shared", Read(WorkspaceFile)[PageId]!["Editor"]!.GetValue<string>());
    }

    /// <summary>
    /// Every successful change reports the scope the value resolves from afterwards, which is not always the scope
    /// that was written: a value moved beneath a higher scope is still hidden by it.
    /// </summary>
    [Fact]
    public void SuccessfulChangesReportTheResultingScope()
    {
        Write(WorkspaceFile, Page("""{"Editor":"workspace"}"""));
        Write(PersonalFile, Page("""{"Editor":"personal"}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        // Beneath a higher scope the moved value is stored but hidden, and the report says so.
        var moved = settings.Move(PageId, "Editor", SettingsScope.Personal, SettingsScope.User);
        Assert.True(moved.Succeeded);
        Assert.Equal(SettingsScope.Workspace, moved.Effective);
        Assert.Equal("workspace", page.Editor);
        Assert.Equal([SettingsScope.User, SettingsScope.Workspace], settings.ScopesHolding(PageId, "Editor"));

        var copied = settings.Copy(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal);
        Assert.Equal(SettingsScope.Personal, copied.Effective);

        var reset = settings.Reset(PageId, "Editor", SettingsScope.Personal);
        Assert.Equal(SettingsScope.Workspace, reset.Effective);

        var cleared = settings.Reset(PageId, "Editor", SettingsScope.Workspace);
        Assert.Equal(SettingsScope.User, cleared.Effective);

        // Clearing a scope that holds nothing is a successful no-op that still reports where the value comes from.
        var nothing = settings.Reset(PageId, "Editor", SettingsScope.Personal);
        Assert.True(nothing.Succeeded);
        Assert.Equal(SettingsScope.User, nothing.Effective);

        var last = settings.Reset(PageId, "Editor", SettingsScope.User);
        Assert.True(last.Succeeded);
        Assert.Null(last.Effective);
        Assert.Equal("builtin", page.Editor);
    }

    /// <summary>Transfers that cannot make sense are refused with a reason and change nothing.</summary>
    [Fact]
    public void RefusedTransfersChangeNothing()
    {
        Write(UserFile, Page("""{"Editor":"user"}"""));
        var settings = Create();
        settings.Register(new LayeredPage());
        settings.Register(new SinglePage());

        Assert.False(settings.Move(PageId, "Editor", SettingsScope.User, SettingsScope.User).Succeeded);
        Assert.False(settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal).Succeeded);
        Assert.False(settings.Move("test.single", "Value", SettingsScope.User, SettingsScope.Personal).Succeeded);
        Assert.False(settings.Move("test.missing", "Value", SettingsScope.User, SettingsScope.Personal).Succeeded);
        Assert.Equal([SettingsScope.User], settings.ScopesHolding(PageId, "Editor"));

        settings.BindWorkspace(null);
        settings.BindPersonal(null);
        var workspace = settings.Copy(PageId, "Editor", SettingsScope.User, SettingsScope.Workspace);
        var personal = settings.Copy(PageId, "Editor", SettingsScope.User, SettingsScope.Personal);
        Assert.Contains("no project is open", workspace.Error, StringComparison.Ordinal);
        Assert.Contains("no identity", personal.Error, StringComparison.Ordinal);
    }

    [EditorSetting("Shared", Id = "test.shared", Workspace = true)]
    sealed class SharedPage
    {
        public int Value { get; [UsedImplicitly] set; }
    }

    /// <summary>A workspace page edited just before another project opens is written to the project being left.</summary>
    [Fact]
    public void RebindingWorkspaceWritesItsPendingEditFirst()
    {
        var settings = Create();
        var page = new SharedPage();
        settings.Register(page);

        page.Value = 6;
        settings.NotifyChanged("test.shared");
        settings.BindWorkspace(Path.Combine(root, "other"));

        Assert.Equal(6, Read(WorkspaceFile)["test.shared"]!["Value"]!.GetValue<int>());
    }

    [EditorSetting("Own", Id = "test.personal", Scope = SettingsScope.Personal, Scopes = SettingsScope.User)]
    sealed class PersonalHomePage
    {
        public int Value { get; [UsedImplicitly] set; }
    }

    /// <summary>
    /// A page homed in a scope that is not bound still saves: its edit goes to the highest scope that is, and
    /// questions about unknown pages or rebinding an unbound scope are answered without error.
    /// </summary>
    [Fact]
    public void EditsFallBackToTheHighestBoundScope()
    {
        var settings = Create(bind: false);
        var page = new PersonalHomePage();
        settings.Register(page);

        page.Value = 4;
        settings.NotifyChanged("test.personal");
        settings.Save();

        Assert.Equal(4, Read(UserFile)["test.personal"]!["Value"]!.GetValue<int>());
        Assert.Empty(settings.ScopesHolding("test.missing", "Value"));
        Assert.Null(settings.EffectiveScope("test.missing", "Value"));
        settings.BindWorkspace(null);
        settings.BindWorkspace(null);
        Assert.False(settings.HasWorkspace);
    }

    /// <summary>A member or value the page cannot hold is refused before any file is touched.</summary>
    [Fact]
    public void WritesRefuseMembersAndValuesThePageCannotHold()
    {
        var settings = Create();
        settings.Register(new LayeredPage());

        Assert.False(settings.Write(PageId, "Missing", SettingsScope.User, 1).Succeeded);
        Assert.False(settings.Write(PageId, "Port", SettingsScope.User, "not a number").Succeeded);
        Assert.False(File.Exists(UserFile));
    }

    /// <summary>A source that cannot be cleared after the destination was written is reported, with the value in both scopes.</summary>
    [Fact]
    public void FailureToClearTheSourceLeavesTheValueInBothScopes()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        var settings = Create();
        settings.Register(new LayeredPage());
        File.WriteAllText(WorkspaceFile, "{broken");

        var change = settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal);

        Assert.False(change.Succeeded);
        Assert.Contains("could not be cleared", change.Error, StringComparison.Ordinal);
        Assert.Equal("shared", Read(PersonalFile)[PageId]!["Editor"]!.GetValue<string>());
        Assert.Equal("{broken", File.ReadAllText(WorkspaceFile));
    }

    /// <summary>Edits made just before the project is switched are written to the project being left.</summary>
    [Fact]
    public void RebindingWritesPendingEditsFirst()
    {
        Write(PersonalFile, Page("""{"Editor":"first"}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);

        page.Editor = "edited";
        settings.NotifyChanged(PageId);
        settings.BindPersonal(Guid.NewGuid().ToString("D"));

        Assert.Equal("edited", Read(PersonalFile)[PageId]!["Editor"]!.GetValue<string>());
    }

    /// <summary>Restoring declared defaults covers properties and fields, and skips what cannot be set.</summary>
    [Fact]
    public void DefaultsRestoreFieldsAndPropertiesAndSkipRejectedSetters()
    {
        var target = new Restorable { Field = 5, Property = "changed" };
        var defaults = new Dictionary<string, object?>
        {
            [nameof(Restorable.Field)] = 1,
            [nameof(Restorable.Property)] = "declared",
            [nameof(Restorable.Rejecting)] = 9,
            ["Missing"] = 3,
        };

        SettingsDefaults.Restore(target, defaults);

        Assert.Equal(1, target.Field);
        Assert.Equal("declared", target.Property);
    }

    sealed class Restorable
    {
        public int Field;

        public string Property { get; set; } = "";

        public int Rejecting
        {
            get => 0;
            [UsedImplicitly]
            set => throw new ArgumentException("rejected");
        }
    }

    /// <summary>
    /// Clearing the last value of a scope deletes its file, its backup and the folder that is left empty,
    /// so a project never carries an empty settings file.
    /// </summary>
    [Fact]
    public void ClearingTheLastValueDeletesTheScopeFile()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        var settings = Create();
        settings.Register(new LayeredPage());
        settings.Write(PageId, "Editor", SettingsScope.Workspace, "again");
        Assert.True(File.Exists(WorkspaceFile + ".bak"));

        Assert.True(settings.Reset(PageId, "Editor", SettingsScope.Workspace).Succeeded);

        Assert.False(File.Exists(WorkspaceFile));
        Assert.False(File.Exists(WorkspaceFile + ".bak"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(WorkspaceFile)));
    }

    /// <summary>Moving the last value out of a scope deletes the source file the same way.</summary>
    [Fact]
    public void MovingTheLastValueDeletesTheSourceFile()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        var settings = Create();
        settings.Register(new LayeredPage());

        Assert.True(settings.Move(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal).Succeeded);

        Assert.False(File.Exists(WorkspaceFile));
        Assert.True(File.Exists(PersonalFile));
    }

    /// <summary>A file that still holds another page, or anything else, is kept when one value is cleared.</summary>
    [Fact]
    public void ClearingAValueKeepsAFileThatHoldsOtherContent()
    {
        Write(WorkspaceFile, """{"other.plugin":{"X":1},""" + Page("""{"Editor":"shared"}""")[1..]);
        var settings = Create();
        settings.Register(new LayeredPage());

        Assert.True(settings.Reset(PageId, "Editor", SettingsScope.Workspace).Succeeded);

        var document = Read(WorkspaceFile);
        Assert.Equal(1, document["other.plugin"]!["X"]!.GetValue<int>());
        Assert.False(document.ContainsKey(PageId));
    }

    /// <summary>A scope that has no page of its own and nothing stored never gets a file, so no empty one is committed.</summary>
    [Fact]
    public void ScopesWithNothingStoredNeverGetAFile()
    {
        var settings = Create();
        settings.Register(new LayeredPage());

        settings.Save();

        Assert.False(File.Exists(WorkspaceFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(WorkspaceFile)));
        Assert.False(File.Exists(PersonalFile));
        Assert.False(File.Exists(UserFile));
    }

    /// <summary>Copying keeps the source.</summary>
    [Fact]
    public void CopyKeepsTheSource()
    {
        Write(WorkspaceFile, Page("""{"Editor":"shared"}"""));
        var settings = Create();
        settings.Register(new LayeredPage());

        Assert.True(settings.Copy(PageId, "Editor", SettingsScope.Workspace, SettingsScope.Personal).Succeeded);

        Assert.Equal([SettingsScope.Workspace, SettingsScope.Personal], settings.ScopesHolding(PageId, "Editor"));
    }

    /// <summary>A page can only be stored in the scopes it declares.</summary>
    [Fact]
    public void PagesRefuseScopesTheyDoNotAllow()
    {
        var settings = Create();
        settings.Register(new SinglePage());

        var change = settings.Write("test.single", "Value", SettingsScope.Personal, 5);

        Assert.False(change.Succeeded);
        Assert.False(File.Exists(PersonalFile));
    }

    /// <summary>Personal values live outside the project and survive the project folder being renamed or cleaned.</summary>
    [Fact]
    public void PersonalValuesLiveOutsideTheProject()
    {
        var settings = Create();
        settings.Register(new LayeredPage());
        settings.Write(PageId, "Editor", SettingsScope.Personal, "mine");

        Assert.StartsWith(root, PersonalFile, StringComparison.Ordinal);
        Assert.False(PersonalFile.StartsWith(ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.True(File.Exists(PersonalFile));
        Assert.Equal(PersonalFile, settings.PathFor(SettingsScope.Personal));

        // The same identity at another path finds the same values.
        var moved = Create(bind: false);
        moved.BindWorkspace(Path.Combine(root, "renamed"));
        moved.BindPersonal(ProjectId);
        var page = new LayeredPage();
        moved.Register(page);

        Assert.Equal("mine", page.Editor);
    }

    /// <summary>Only a GUID names a personal directory, so an identity cannot escape its folder.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData(@"..\elsewhere")]
    [InlineData(@"C:\elsewhere")]
    [InlineData("/elsewhere")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void OnlyGuidsBindThePersonalScope(string identity)
    {
        var settings = Create();
        settings.BindPersonal(identity);

        Assert.False(settings.HasPersonal);
        Assert.Equal(string.Empty, settings.PathFor(SettingsScope.Personal));
    }

    /// <summary>
    /// Windows and macOS file systems ignore case, so the same identity written in any case must reach the same
    /// folder rather than split one project's values in two.
    /// </summary>
    [Fact]
    public void IdentityCaseDoesNotChangeTheFolder()
    {
        var settings = Create(bind: false);
        settings.BindPersonal(ProjectId.ToUpperInvariant());
        var upper = settings.PathFor(SettingsScope.Personal);
        settings.BindPersonal(ProjectId.ToLowerInvariant());

        Assert.Equal(upper, settings.PathFor(SettingsScope.Personal));
        Assert.Equal(PersonalFile, upper);
    }

    /// <summary>Opening another project swaps its personal values in and clears the previous project's.</summary>
    [Fact]
    public void RebindingPersonalSwitchesProjects()
    {
        var other = Guid.NewGuid().ToString("D");
        Write(PersonalFile, Page("""{"Editor":"first"}"""));
        Write(Path.Combine(root, "projects", other, "settings.json"), Page("""{"Port":5}"""));
        var settings = Create();
        var page = new LayeredPage();
        settings.Register(page);
        Assert.Equal("first", page.Editor);

        settings.BindPersonal(other);

        Assert.Equal("builtin", page.Editor);
        Assert.Equal(5, page.Port);
    }

    /// <summary>A Studio-scope value is a default that every other scope overrides.</summary>
    [Fact]
    public void StudioIsTheLowestScope()
    {
        Write(StudioFile, Page("""{"Port":8,"Editor":"studio"}"""));
        Write(UserFile, Page("""{"Editor":"user"}"""));
        var page = new LayeredPage();

        Create().Register(page);

        Assert.Equal(8, page.Port);
        Assert.Equal("user", page.Editor);
    }
}
