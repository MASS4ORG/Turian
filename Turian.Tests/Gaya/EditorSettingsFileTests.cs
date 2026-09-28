namespace Turian.Tests;

/// <summary>Tests for how the editor settings file is written.</summary>
public class EditorSettingsFileTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"gaya-settings-file-{Guid.NewGuid():N}.json");

    sealed class LanguagePage
    {
        public int Language { get; set; }
    }

    /// <summary>Deletes the file.</summary>
    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
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
}
