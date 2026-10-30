namespace Turian.Tests;

/// <summary>Tests that the Studio loads a project's <c>.strings</c> tables from the catalog without the game brick.</summary>
public sealed class ProjectStringTablesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-project-tables").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    sealed class TableAsset : Asset;

    sealed class FixedPolicy(string? locale) : IProjectLocalePolicy
    {
        public string? DefaultLocale(IAppSettings settings) => locale;
    }

    const string PortugueseTable =
        """{"locale":"pt-BR","entries":[{"key":"play","source":"Play","translation":"Jogar"}]}""";

    AssetDatabase Register(params (string Name, string Json)[] files)
    {
        var database = new AssetDatabase();
        foreach (var (name, json) in files)
        {
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName, name);
            File.WriteAllText(path, json);
            Assert.True(database.RegisterAsset(new TableAsset { RelativePath = path }, path));
        }

        return database;
    }

    static void Load(LocaleService service, AssetDatabase database, params IProjectLocalePolicy[] policies) =>
        ProjectStringTables.Load(service, database, new AppSettings(), policies, NullLogger.Instance);

    /// <summary>Every readable table is added, and with no policy the preview stays in the default locale.</summary>
    [Fact]
    public void Load_AddsEveryStringsTableFromTheCatalog()
    {
        var database = Register(("pt-BR.strings", PortugueseTable), ("notes.txt", "not a table"));
        var service = new LocaleService();

        Load(service, database);

        Assert.Equal(["pt-BR"], service.AvailableLocales);
        Assert.Equal("en", service.ActiveLocale);
    }

    /// <summary>A project whose policy chooses pt-BR previews in pt-BR.</summary>
    [Fact]
    public void Load_StartsThePreviewInThePolicysDefaultLocale()
    {
        var database = Register(("pt-BR.strings", PortugueseTable));
        var service = new LocaleService();

        Load(service, database, new FixedPolicy(null), new FixedPolicy("pt-BR"), new FixedPolicy("fr"));

        Assert.Equal("pt-BR", service.ActiveLocale);
        Assert.Equal("Jogar", service.Translate("play"));
    }

    /// <summary>Reopening a project without a policy returns the preview to the default locale.</summary>
    [Fact]
    public void Load_ResetsThePreviewLocaleWhenNoPolicyChoosesOne()
    {
        var database = Register(("pt-BR.strings", PortugueseTable));
        var service = new LocaleService();
        Load(service, database, new FixedPolicy("pt-BR"));

        Load(service, database);

        Assert.Equal("en", service.ActiveLocale);
    }

    /// <summary>A damaged table is skipped, the others still load and earlier tables are replaced.</summary>
    [Fact]
    public void Load_SkipsDamagedTablesAndReplacesPreviousOnes()
    {
        var database = Register(
            ("broken.strings", "{ not json"),
            ("fr.strings", """{"locale":"fr","entries":[{"key":"play","source":"Play","translation":"Jouer"}]}"""));
        var service = new LocaleService();
        service.AddTable(new StringTable("de"));

        Load(service, database);

        Assert.Equal(["fr"], service.AvailableLocales);
    }

    /// <summary>Policy discovery ignores assemblies that do not reference the editor.</summary>
    [Fact]
    public void Discover_IgnoresAssembliesWithoutTheEditorContract() =>
        Assert.Empty(ProjectLocalePolicies.Discover([typeof(string).Assembly], NullLogger.Instance));
}
