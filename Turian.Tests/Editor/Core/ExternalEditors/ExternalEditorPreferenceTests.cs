namespace Turian.Tests;

/// <summary>
/// The headless preference reads and writes the same files as Studio's settings, so a value set from the command line
/// is what Studio shows and a value changed in Studio is what the command line reads.
/// </summary>
public sealed class ExternalEditorPreferenceTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-editor-preference-").FullName;

    string Config => Path.Combine(root, "config");

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    async Task<string> CreateProject() =>
        (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game é")))!;

    ExternalEditorPreference Open(string project) => new(project, Config);

    static ExternalEditorChoice Choice(string editor, string executable = "/usr/bin/editor", string arguments = "") =>
        new(editor, executable, arguments);

    /// <summary>The highest scope that holds a choice applies, and clearing a scope lets the one below show.</summary>
    [Fact]
    public async Task HighestScopeWinsAndClearingFallsThrough()
    {
        var preference = Open(await CreateProject());
        Assert.Null(preference.Resolve().Scope);
        Assert.True(preference.Resolve().Choice.UsesSystemDefault);

        foreach (var scope in preference.Scopes)
        {
            Assert.True(preference.Write(scope, Choice(scope.ToString().ToLowerInvariant())));
        }

        Assert.Equal([SettingsScope.Studio, SettingsScope.User, SettingsScope.Workspace, SettingsScope.Personal],
            preference.Holding());
        foreach (var scope in SettingsScopes.Ascending.Reverse())
        {
            var (choice, winner) = preference.Resolve();
            Assert.Equal(scope, winner);
            Assert.Equal(scope.ToString().ToLowerInvariant(), choice.EditorId);
            Assert.True(preference.Clear(scope));
        }

        Assert.Null(preference.Resolve().Scope);
        Assert.False(File.Exists(preference.PathFor(SettingsScope.Workspace)));
    }

    /// <summary>
    /// Cascade tripwire. The scope order is one constant, but "the highest scope that holds the value wins" is written
    /// once for Studio's settings and once for the headless preference. For every non-empty combination of scopes both
    /// must name the same winner, and that winner must be the highest one in the order the cascade is documented to have.
    /// </summary>
    [Fact]
    public async Task EveryCombinationOfScopesResolvesTheSameInStudioAndHeadlessly()
    {
        SettingsScope[] documented = [SettingsScope.Studio, SettingsScope.User, SettingsScope.Workspace, SettingsScope.Personal];
        Assert.Equal(documented, SettingsScopes.Ascending);
        Assert.True(documented.Zip(documented.Skip(1)).All(static pair => pair.First < pair.Second));
        var project = await CreateProject();
        var preference = Open(project);

        for (var combination = 1; combination < 1 << documented.Length; combination++)
        {
            foreach (var scope in documented)
            {
                Assert.True(preference.Clear(scope));
            }

            var held = documented.Where((_, index) => (combination & (1 << index)) != 0).ToList();
            foreach (var scope in held)
            {
                Assert.True(preference.Write(scope, Choice(scope.ToString().ToLowerInvariant())));
            }

            var (choice, headless) = preference.Resolve();
            var studio = Studio(project, out var page);

            Assert.Equal(held[^1], headless);
            Assert.Equal(held[^1], studio.EffectiveScope(ExternalEditorSettings.PageId, ExternalEditorSettings.Member));
            Assert.Equal(choice, page.Editor);
        }
    }

    /// <summary>
    /// The editor's options are part of the one stored choice, so they live in the same scope as the selection, move and
    /// copy with it, and leave with it when a reset deletes the scope's file.
    /// </summary>
    [Fact]
    public async Task OptionsTravelWithTheChoiceThroughMoveCopyAndReset()
    {
        var project = await CreateProject();
        var choice = new ExternalEditorChoice("code", "/x/code", "", new Dictionary<string, string> { ["reuse"] = "true" });
        var settings = Studio(project, out _);
        var preference = Open(project);
        const string page = ExternalEditorSettings.PageId;
        const string member = ExternalEditorSettings.Member;

        Assert.True(settings.Write(page, member, SettingsScope.Workspace, choice).Succeeded);
        Assert.True(settings.Move(page, member, SettingsScope.Workspace, SettingsScope.Personal).Succeeded);
        Assert.Null(preference.Read(SettingsScope.Workspace));
        Assert.Equal(choice, preference.Read(SettingsScope.Personal));

        Assert.True(settings.Copy(page, member, SettingsScope.Personal, SettingsScope.User).Succeeded);
        Assert.Equal(choice, preference.Read(SettingsScope.User));

        Assert.True(settings.Reset(page, member, SettingsScope.Personal).Succeeded);
        Assert.False(File.Exists(preference.PathFor(SettingsScope.Personal)));
        Assert.Equal(SettingsScope.User, preference.Resolve().Scope);
        Assert.True(settings.Reset(page, member, SettingsScope.User).Succeeded);
        Assert.Null(preference.Resolve().Scope);
        Assert.Null(preference.Resolve().Choice.Options);
    }

    /// <summary>A scope holding something that is not a choice is skipped, so the scope below applies.</summary>
    [Fact]
    public async Task UnreadableChoiceFallsThroughToTheScopeBelow()
    {
        var preference = Open(await CreateProject());
        Assert.True(preference.Write(SettingsScope.User, Choice("code")));
        new SettingsFile(preference.PathFor(SettingsScope.Personal)!).Update(ExternalEditorSettings.PageId,
            new Dictionary<string, JsonNode?> { [ExternalEditorSettings.Member] = "not a choice" }, []);

        Assert.Null(preference.Read(SettingsScope.Personal));
        Assert.Null(preference.Read(SettingsScope.Workspace));
        var (choice, scope) = preference.Resolve();

        Assert.Equal("code", choice.EditorId);
        Assert.Equal(SettingsScope.User, scope);
    }

    /// <summary>A project without a project asset has no identity, so no personal scope to write.</summary>
    [Fact]
    public void ProjectWithoutAnIdentityHasNoPersonalScope()
    {
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var preference = Open(empty);

        Assert.DoesNotContain(SettingsScope.Personal, preference.Scopes);
        Assert.Null(preference.PathFor(SettingsScope.Personal));
        Assert.False(preference.Write(SettingsScope.Personal, Choice("code")));
        Assert.True(preference.Write(SettingsScope.User, Choice("code")));
    }

    /// <summary>A value written headlessly is what Studio's settings resolve, from the same scope.</summary>
    [Fact]
    public async Task ValueWrittenHeadlesslyIsVisibleToStudioSettings()
    {
        var project = await CreateProject();
        var preference = Open(project);
        var choice = Choice("custom", "/opt/Mi Editor é/ed", "--open \"{file}\" --line {line}");
        Assert.True(preference.Write(SettingsScope.Workspace, choice));

        var settings = Studio(project, out var page);

        Assert.Equal(choice, page.Editor);
        Assert.Equal(SettingsScope.Workspace, settings.EffectiveScope(ExternalEditorSettings.PageId,
            ExternalEditorSettings.Member));
        Assert.Equal(SettingsPaths.Workspace(project), settings.PathFor(SettingsScope.Workspace));
    }

    /// <summary>A value changed in Studio is what the headless preference resolves.</summary>
    [Fact]
    public async Task ValueChangedInStudioIsVisibleHeadlessly()
    {
        var project = await CreateProject();
        var settings = Studio(project, out var page);
        var choice = Choice("custom", "C:\\Program Files\\Éditeur\\ed.exe", "{file}");

        page.Editor = choice;
        settings.NotifyChanged(ExternalEditorSettings.PageId);
        settings.Save();

        var (resolved, scope) = Open(project).Resolve();
        Assert.Equal(choice, resolved);
        Assert.Equal(SettingsScope.Personal, scope);
    }

    /// <summary>A running Studio picks up a value the command line writes, without a restart.</summary>
    [Fact]
    public async Task RunningStudioPicksUpACommandLineWrite()
    {
        var project = await CreateProject();
        var settings = Studio(project, out var page);
        settings.ExternalChangeInterval = TimeSpan.Zero;

        Assert.True(Open(project).Write(SettingsScope.Personal, Choice("code")));
        settings.Flush();

        Assert.Equal("code", page.Editor.EditorId);
    }

    EditorSettings Studio(string project, out ExternalEditorSettings page)
    {
        var settings = new EditorSettings(NullLogger.Instance, SettingsPaths.User(Config),
            personalRoot: Path.Combine(Config, SettingsPaths.PersonalDirectory));
        page = new ExternalEditorSettings();
        settings.Register(page);
        settings.BindWorkspace(project);
        settings.BindPersonal(ProjectAsset.Load(Path.Combine(project, "Assets"))!.Id.ToString("D"));
        return settings;
    }
}
