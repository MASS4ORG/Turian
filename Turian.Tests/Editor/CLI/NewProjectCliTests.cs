using System.CommandLine;

namespace Turian.Tests;

/// <summary>Project creation accepts repository metadata without replacing existing content.</summary>
[Collection(SerialTests.Name)]
public sealed class NewProjectCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-new-cli-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A fresh clone or repository with documentation can receive a valid scaffold.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesInsideRepositoryAndKeepsMetadata(bool documented)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var files = new List<string> { ".git/config" };
        if (documented)
        {
            Directory.CreateDirectory(Path.Combine(root, ".github", "workflows"));
            files.AddRange(["README.md", ".gitignore", ".gitattributes", "LICENSE", "LICENSE.md",
                ".gitlab-ci.yml", ".github/workflows/build.yml"]);
        }

        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(root, file), $"original {file}", TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await Create());

        Assert.Empty(ProjectValidator.Validate(root));
        Assert.True(File.Exists(Path.Combine(root, "Assets", "Game.cs")));
        foreach (var file in files)
        {
            Assert.Equal($"original {file}", await File.ReadAllTextAsync(Path.Combine(root, file),
                TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Running new twice rejects the existing project without changing any of its files.</summary>
    [Fact]
    public async Task RejectsExistingProjectWithoutOverwriting()
    {
        Assert.Equal(0, await Create());
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(static path => path, File.ReadAllBytes);

        Assert.Equal(1, await Create());

        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Arbitrary files and directories disguised as documentation are rejected before scaffolding.</summary>
    [Theory]
    [InlineData("game.txt", false)]
    [InlineData("README.md", true)]
    [InlineData("LICENSE", true)]
    public async Task RejectsNonMetadata(string name, bool directory)
    {
        var path = Path.Combine(root, name);
        if (directory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, "keep me", TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await Create());
        Assert.False(Directory.Exists(Path.Combine(root, "Assets")));
        Assert.True(directory ? Directory.Exists(path) : File.ReadAllText(path) == "keep me");
    }

    Task<int> Create()
    {
        var factory = typeof(global::Turian.Editor.CLI.Program)
            .GetMethod("NewCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
        var command = (Command)factory.Invoke(null, null)!;
        return new RootCommand { command }.Parse(["new", root])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
    }
}
