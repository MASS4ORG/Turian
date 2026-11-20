namespace Turian.Tests;

/// <summary>Editors are found on <c>PATH</c> first, then in the folders they usually install to.</summary>
public sealed class ExecutableLocatorTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-locator-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    string Touch(string folder, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, folder, "with space é")).FullName;
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    static Func<string, string?> Environment(string? path, string? pathExt = null) =>
        name => name switch { "PATH" => path, "PATHEXT" => pathExt, _ => null };

    /// <summary><c>PATH</c> wins over the standard folders, including folders with spaces and non-ASCII names.</summary>
    [Fact]
    public void PathComesBeforeStandardFolders()
    {
        var onPath = Touch("path", "editor");
        var standard = Touch("standard", "editor");

        var found = ExecutableLocator.Find(["editor"], [Path.GetDirectoryName(standard)!],
            Environment(Path.GetDirectoryName(onPath)), windows: false);

        Assert.Equal(onPath, found);
    }

    /// <summary>The standard folders are searched when <c>PATH</c> has nothing, and absent ones are skipped.</summary>
    [Fact]
    public void FallsBackToStandardFolders()
    {
        var standard = Touch("standard", "editor");

        var found = ExecutableLocator.Find(["editor"],
            [Path.Combine(root, "missing"), Path.GetDirectoryName(standard)!], Environment(null), windows: false);

        Assert.Equal(standard, found);
    }

    /// <summary>The first name in the preference order that exists wins.</summary>
    [Fact]
    public void NamesAreTriedInOrder()
    {
        Touch("bin", "second");
        var first = Touch("bin", "first");

        Assert.Equal(first, ExecutableLocator.Find(["missing", "first", "second"],
            [Path.GetDirectoryName(first)!], Environment(null), windows: false));
        Assert.Null(ExecutableLocator.Find(["missing"], [Path.GetDirectoryName(first)!], Environment(null),
            windows: false));
    }

    /// <summary>On Windows the <c>PATHEXT</c> extensions are tried, and an extensionless script file is not preferred.</summary>
    [Fact]
    public void WindowsTriesPathExtensionsBeforeTheBareName()
    {
        var script = Touch("bin", "code");
        var command = Touch("bin", "code.cmd");

        var found = ExecutableLocator.Find(["code"], [], Environment(Path.GetDirectoryName(script), ".exe;.cmd"),
            windows: true);

        Assert.Equal(command, found);
    }

    /// <summary>Quoted <c>PATH</c> entries, as Windows allows, are unquoted.</summary>
    [Fact]
    public void QuotedPathEntriesAreUnquoted()
    {
        var tool = Touch("bin", "tool");

        Assert.Equal(tool, ExecutableLocator.Find(["tool"], [],
            Environment($"\"{Path.GetDirectoryName(tool)}\""), windows: false));
    }
}
