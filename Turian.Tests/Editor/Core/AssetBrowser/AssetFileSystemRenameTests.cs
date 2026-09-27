namespace Turian.Tests;

/// <summary>Tests for renaming in the asset browser.</summary>
public class AssetFileSystemRenameTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-rename-{Guid.NewGuid():N}");
    readonly AssetFileSystem fileSystem = new(new SettingsService(), assetImporter: null!);

    /// <summary>Deletes the folder the test worked in.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Changing only the letter case of a folder renames it.</summary>
    [Fact]
    public void Rename_FolderCaseOnly_RenamesIt()
    {
        var folder = Path.Combine(root, "GAme");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "x");

        var renamed = fileSystem.Rename(folder, isDirectory: true, "Game");

        Assert.Equal(Path.Combine(root, "Game"), renamed);
        Assert.Equal(["Game"], Directory.GetDirectories(root).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(root, "Game", "keep.txt")));
    }
}
