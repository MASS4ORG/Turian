namespace Turian.Tests;

/// <summary>Checks the game folder and executable layout delivered to players.</summary>
public sealed class ExportLayoutTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-export-layout-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Default exports package one game-named folder, including when a profile is active.</summary>
    [Theory]
    [InlineData("turian-sample-1", "turian-sample-1", "", false)]
    [InlineData("turian-sample-1", "turian-sample-1", ".exe", true)]
    [InlineData("My Game: Deluxe", "My Game  Deluxe", ".exe", true)]
    [InlineData("Game/Name", "Game Name", "", true)]
    [InlineData("Game\\Name", "Game Name", ".exe", true)]
    public void ArchiveContainsTheGameFolderAndExecutable(string title, string filename, string extension,
        bool hasProfile)
    {
        var settings = new BuildAppSettings { Title = title, ProjectAbsoluteDir = root };
        var profile = hasProfile ? new ProjectBuildProfile("Client", "client.dataasset", new BuildProfile()) : null;
        var exporter = new ExportUserCode(settings, NullLogger.Instance, profile: profile);
        var expected = Path.Combine(root, "Output", filename);
        Assert.Equal(expected, exporter.OutputDirectory);
        Directory.CreateDirectory(exporter.OutputDirectory);
        File.WriteAllText(Path.Combine(exporter.OutputDirectory, $"launcher{extension}"), "executable");
        BuildFinalizer.RenameExecutable(new BuildPlatformContext(settings, NullLogger.Instance, "launcher.csproj",
            exporter.OutputDirectory, "Release", profile?.Profile), extension);

        var archive = Path.Combine(root, "game.zip");
        ZipFile.CreateFromDirectory(exporter.OutputDirectory, archive, CompressionLevel.Fastest,
            includeBaseDirectory: true);

        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal($"{filename}/{filename}{extension}", Assert.Single(zip.Entries).FullName);
    }

    /// <summary>Explicit destinations keep profile builds isolated while preserving the game folder name.</summary>
    [Fact]
    public void ExplicitOutputKeepsProfilesSeparate()
    {
        var settings = new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = root };
        var output = Path.Combine(root, "Output", "Server", "Game");
        var profile = new ProjectBuildProfile("Server", "server.dataasset", new BuildProfile
        {
            Target = BuildTarget.Server
        });

        Assert.Equal(output, new ExportUserCode(settings, NullLogger.Instance, profile: profile,
            outputDirectory: output).OutputDirectory);
    }
}
