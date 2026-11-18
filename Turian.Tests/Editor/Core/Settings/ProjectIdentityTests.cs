namespace Turian.Tests;

/// <summary>
/// A project's identity is the id of its project asset, and the user's personal settings are keyed by it. Every project
/// must therefore have its own, which a copy or a template instance only gets by renewing it.
/// </summary>
public sealed class ProjectIdentityTests : IDisposable
{
    const string PageId = "test.identity";

    readonly string root = Directory.CreateTempSubdirectory("turian-identity-").FullName;

    [Gaya.EditorSetting("Editor", Id = PageId, Scope = SettingsScope.User, Scopes = SettingsScope.Personal)]
    sealed class EditorPage
    {
        public string Editor { get; [UsedImplicitly] set; } = "builtin";
    }

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    async Task<string> Create(string name) =>
        (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, name)))!;

    static Guid IdentityOf(string project) => ProjectAsset.Load(Path.Combine(project, "Assets"))!.Id;

    static AppSettings Settings(string project) => new() { ProjectAbsoluteDir = project };

    static void CopyTree(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>Two new projects never share an identity.</summary>
    [Fact]
    public async Task NewProjectsHaveTheirOwnIdentity()
    {
        var first = IdentityOf(await Create("first"));
        var second = IdentityOf(await Create("second"));

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
    }

    /// <summary>A copied folder is the same project until it is renewed; renewing changes the identity and nothing else.</summary>
    [Fact]
    public async Task RenewingACopyChangesOnlyItsIdentity()
    {
        var original = await Create("original");
        var copy = Path.Combine(root, "copy");
        CopyTree(original, copy);
        var before = IdentityOf(original);
        Assert.Equal(before, IdentityOf(copy));
        var registry = ProjectAsset.Load(Path.Combine(copy, "Assets"))!;

        var renewed = ProjectSettingsFiles.RenewIdentity(Settings(copy));

        Assert.NotNull(renewed);
        Assert.NotEqual(before, renewed);
        Assert.Equal(renewed, IdentityOf(copy));
        Assert.Equal(renewed, Asset.Load(Path.Combine(copy, "Assets", "Project.dataasset.meta"))!.Id);
        Assert.Equal(before, IdentityOf(original));
        var after = ProjectAsset.Load(Path.Combine(copy, "Assets"))!;
        Assert.Equal(registry.Settings.Select(static s => s.AssetId), after.Settings.Select(static s => s.AssetId));
        Assert.Equal(registry.BuildProfiles.Select(static s => s.AssetId), after.BuildProfiles.Select(static s => s.AssetId));
        Assert.Equal(registry.EngineVersion, after.EngineVersion);
    }

    /// <summary>A copied project keeps the original's personal values only until renewed, then has a store of its own.</summary>
    [Fact]
    public async Task CopiedProjectGetsItsOwnPersonalStore()
    {
        var original = await Create("original");
        var copy = Path.Combine(root, "copy");
        CopyTree(original, copy);
        ProjectSettingsFiles.RenewIdentity(Settings(copy));
        var personalRoot = Path.Combine(root, "personal");
        var settings = new EditorSettings(NullLogger.Instance, Path.Combine(root, "settings.json"),
            Path.Combine(root, "studio.json"), personalRoot);
        var page = new EditorPage();
        settings.Register(page);

        settings.BindPersonal(IdentityOf(original).ToString("D"));
        Assert.True(settings.Write(PageId, "Editor", SettingsScope.Personal, "original's").Succeeded);
        var originalFile = settings.PathFor(SettingsScope.Personal);

        settings.BindPersonal(IdentityOf(copy).ToString("D"));
        var copyFile = settings.PathFor(SettingsScope.Personal);
        Assert.NotEqual(originalFile, copyFile);
        Assert.Equal("builtin", page.Editor);
        Assert.True(settings.Write(PageId, "Editor", SettingsScope.Personal, "copy's").Succeeded);

        settings.BindPersonal(IdentityOf(original).ToString("D"));
        Assert.Equal("original's", page.Editor);
        settings.BindPersonal(IdentityOf(copy).ToString("D"));
        Assert.Equal("copy's", page.Editor);
        Assert.Equal(2, Directory.GetDirectories(personalRoot).Length);
    }

    /// <summary>A project without a project asset has no identity to renew.</summary>
    [Fact]
    public void ProjectWithoutAProjectAssetHasNothingToRenew()
    {
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;

        Assert.Null(ProjectSettingsFiles.RenewIdentity(Settings(empty)));
    }

    /// <summary>The CLI command renews the project in place and prints the new identity.</summary>
    [Fact]
    public async Task CommandRenewsTheIdentity()
    {
        var project = await Create("cli");
        var before = IdentityOf(project);
        var argument = new System.CommandLine.Argument<FileSystemInfo>("ProjectPath");
        var command = (System.CommandLine.Command)typeof(global::Turian.Editor.CLI.Program)
            .GetMethod("NewIdentityCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [argument])!;
        var cli = new System.CommandLine.RootCommand { command };

        var exitCode = await cli.Parse(["new-identity", project])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotEqual(before, IdentityOf(project));
    }
}
