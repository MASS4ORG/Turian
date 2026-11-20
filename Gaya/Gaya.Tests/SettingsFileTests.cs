namespace Gaya.Tests;

/// <summary>The shared settings file engine, which the studio and headless tools both write through.</summary>
public sealed class SettingsFileTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-settings-file-engine-{Guid.NewGuid():N}");

    string FilePath => Path.Combine(root, "scope", "settings.json");

    /// <summary>Deletes everything the test wrote.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static Dictionary<string, JsonNode?> Set(string member, JsonNode? value) => new() { [member] = value };

    /// <summary>Clearing a member from a file and folder that never existed succeeds and creates nothing.</summary>
    [Fact]
    public void ClearingFromAScopeThatNeverExistedSucceedsAndCreatesNothing()
    {
        var file = new SettingsFile(FilePath);

        Assert.True(file.Update("page", new Dictionary<string, JsonNode?>(), ["Member"]));

        Assert.False(File.Exists(FilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(FilePath)));
    }

    /// <summary>Members are matched without regard to case, and a changed file is noticed by timestamp.</summary>
    [Fact]
    public void MembersMatchIgnoringCaseAndExternalChangesAreNoticed()
    {
        var file = new SettingsFile(FilePath);
        Assert.True(file.Update("page", Set("Editor", "a"), []));
        Assert.True(file.Has("page", "editor"));
        Assert.False(file.ChangedOnDisk());

        new SettingsFile(FilePath).Update("page", Set("Editor", "b"), []);

        Assert.True(file.ChangedOnDisk());
        file.Invalidate();
        Assert.Equal("b", file.Get("page", "EDITOR")!.GetValue<string>());
    }

    /// <summary>Removing the last member deletes the file, its backup and the folder; other pages keep the file.</summary>
    [Fact]
    public void LastMemberRemovalDeletesTheFileButOtherPagesKeepIt()
    {
        var file = new SettingsFile(FilePath);
        file.Update("one", Set("A", 1), []);
        file.Update("two", Set("B", 2), []);
        Assert.True(File.Exists($"{FilePath}.bak"));

        file.Update("one", new Dictionary<string, JsonNode?>(), ["A"]);
        Assert.True(File.Exists(FilePath));

        file.Update("two", new Dictionary<string, JsonNode?>(), ["B"]);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists($"{FilePath}.bak"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(FilePath)));
    }

    /// <summary>An unreadable file is left exactly as it was and reported.</summary>
    [Fact]
    public void UnreadableFileIsLeftAloneAndReported()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{broken");
        var reported = new List<string>();
        var file = new SettingsFile(FilePath, (message, _) => reported.Add(message));

        Assert.False(file.Update("page", Set("A", 1), []));
        Assert.Null(file.Load());

        Assert.Equal("{broken", File.ReadAllText(FilePath));
        Assert.Equal(2, reported.Count);
    }

    /// <summary>Each scope's file path follows the same rules everywhere, and only a GUID names a personal folder.</summary>
    [Fact]
    public void PathsFollowTheScopeRules()
    {
        var id = Guid.NewGuid();

        Assert.Equal(Path.Combine(root, "settings.json"), SettingsPaths.User(root));
        Assert.Equal(Path.Combine(root, "studio.json"), SettingsPaths.Studio(root));
        Assert.Equal(Path.Combine(root, ".gaya", "settings.json"), SettingsPaths.Workspace(root));
        Assert.Equal(Path.Combine(root, "projects", id.ToString("D"), "settings.json"),
            SettingsPaths.Personal(id.ToString("N").ToUpperInvariant(), Path.Combine(root, "projects")));
        Assert.Null(SettingsPaths.Personal("..", root));
        Assert.Null(SettingsPaths.Personal(null, root));
    }
}
