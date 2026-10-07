namespace Turian.Tests;

/// <summary>Tests for how the editor settings file is written.</summary>
public class EditorSettingsFileTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"gaya-settings-file-{Guid.NewGuid():N}.json");

    sealed class LanguagePage
    {
        public int Language { get; [UsedImplicitly] set; }
    }

    /// <summary>Deletes the file.</summary>
    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        GC.SuppressFinalize(this);
    }

    /// <summary>A stored value is read back, rewritten in one step, and no temporary file is left behind.</summary>
    [Fact]
    public void Save_KeepsStoredValuesAndLeavesNoTemporaryFile()
    {
        File.WriteAllText(path, """{ "test.language": { "Language": 1 } }""");
        var page = new LanguagePage();
        var settings = new EditorSettings(NullLogger.Instance, path);

        settings.Register(new SettingsPageDescriptor("test.language", "Language", page));
        settings.Save();

        Assert.Equal(1, page.Language);
        Assert.Contains("\"Language\": 1", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists($"{path}.tmp"));
    }

    /// <summary>Saving a shell host preserves unloaded plugin pages and unknown members.</summary>
    [Fact]
    public void SavePreservesUnloadedPreferences()
    {
        var original = """{"test.language":{"Language":1,"extra":true},"recent":{"Paths":["project"]}}""";
        File.WriteAllText(path, original);
        var settings = new EditorSettings(NullLogger.Instance, path);
        var page = new LanguagePage();
        settings.Register(new SettingsPageDescriptor("test.language", "Language", page));
        page.Language = 2;
        settings.Save();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("project", document["recent"]!["Paths"]![0]!.GetValue<string>());
        Assert.True(document["test.language"]!["extra"]!.GetValue<bool>());
        Assert.Equal(2, document["test.language"]!["Language"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllText(path + ".bak"));
    }

    /// <summary>An unchanged page in an older Studio instance does not overwrite a newer preference.</summary>
    [Fact]
    public void OlderInstanceKeepsNewerPreferences()
    {
        File.WriteAllText(path, """{"test.language":{"Language":1}}""");
        var older = new EditorSettings(NullLogger.Instance, path);
        older.Register(new SettingsPageDescriptor("test.language", "Language", new LanguagePage()));
        var newer = new EditorSettings(NullLogger.Instance, path);
        var language = new LanguagePage();
        newer.Register(new SettingsPageDescriptor("test.language", "Language", language));
        language.Language = 2;
        newer.Save();
        older.Save();
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["test.language"]!["Language"]!.GetValue<int>());
    }

    /// <summary>A malformed preference file remains available for recovery rather than being replaced by defaults.</summary>
    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void SavePreservesUnreadablePreferences(string content)
    {
        File.WriteAllText(path, content);
        var settings = new EditorSettings(NullLogger.Instance, path);
        settings.Register(new SettingsPageDescriptor("test.language", "Language", new LanguagePage()));
        settings.Save();
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>The test host's default preferences and layout paths stay inside its isolated configuration directory.</summary>
    [Fact]
    public void TestsUseIsolatedPreferences()
    {
        var directory = Environment.GetEnvironmentVariable("GAYA_CONFIG_HOME")!;
        Assert.Contains("turian-test-preferences-", directory);
        Assert.Equal(Path.Combine(directory, "settings.json"),
            new EditorSettings(NullLogger.Instance).PathFor(SettingsScope.User));
        Assert.Equal(Path.Combine(directory, "layout.json"), new WorkbenchLayoutStore(NullLogger.Instance).Path);
    }
}
