namespace Turian.Tests;

/// <summary>Tests that the Studio loads a project's <c>.strings</c> tables from the catalog without the game brick.</summary>
public sealed class ProjectStringTablesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-project-tables").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    sealed class TableAsset : Asset;

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

    /// <summary>Every readable table is added and the locale can switch to it.</summary>
    [Fact]
    public void Load_AddsEveryStringsTableFromTheCatalog()
    {
        var database = Register(
            ("pt-BR.strings", """{"locale":"pt-BR","entries":[{"key":"play","source":"Play","translation":"Jogar"}]}"""),
            ("notes.txt", "not a table"));
        var service = new LocaleService();

        ProjectStringTables.Load(service, database, NullLogger.Instance);
        service.SetLocale("pt-BR");

        Assert.Equal(["pt-BR"], service.AvailableLocales);
        Assert.Equal("Jogar", service.Translate("play"));
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

        ProjectStringTables.Load(service, database, NullLogger.Instance);

        Assert.Equal(["fr"], service.AvailableLocales);
    }
}
