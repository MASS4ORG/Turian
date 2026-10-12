namespace Turian.Tests;

/// <summary>Where builds find <c>dotnet</c>: the preferred folder, then <c>DOTNET_ROOT</c>, then <c>PATH</c>.</summary>
public sealed class DotnetLocatorTests : IDisposable
{
    static readonly string Executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    readonly string directory = Directory.CreateTempSubdirectory("turian-dotnet-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    string Install(string name, params string[] sdks)
    {
        var root = Path.Combine(directory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Executable), string.Empty);
        foreach (var sdk in sdks) Directory.CreateDirectory(Path.Combine(root, "sdk", sdk));
        return root;
    }

    static Func<string, string?> Environment(string? dotnetRoot, params string[] path) => name => name switch
    {
        "DOTNET_ROOT" => dotnetRoot,
        "PATH" => string.Join(Path.PathSeparator, path),
        _ => null,
    };

    /// <summary>The preferred folder beats <c>DOTNET_ROOT</c>, which beats <c>PATH</c>.</summary>
    [Fact]
    public void OrderIsPreferredThenDotnetRootThenPath()
    {
        var preferred = Install("preferred");
        var root = Install("root");
        var onPath = Install("path");

        Assert.Equal(Path.Combine(preferred, Executable), DotnetLocator.Find(preferred, null, Environment(root, onPath)));
        Assert.Equal(Path.Combine(root, Executable), DotnetLocator.Find(null, null, Environment(root, onPath)));
        Assert.Equal(Path.Combine(onPath, Executable), DotnetLocator.Find(null, null, Environment(null, onPath)));
        Assert.Equal(Path.Combine(onPath, Executable),
            DotnetLocator.Find(Path.Combine(directory, "missing"), null, Environment(Path.Combine(directory, "none"), onPath)));
    }

    /// <summary>With nothing installed and no version required, the bare command is used as before.</summary>
    [Fact]
    public void FallsBackToTheBareCommand() =>
        Assert.Equal("dotnet", DotnetLocator.Find(null, null, Environment(null)));

    /// <summary>A required SDK major skips installations without it and takes the first that has it.</summary>
    [Fact]
    public void RequiredSdkSelectsTheInstallationHavingIt()
    {
        var old = Install("old", "10.0.112");
        var current = Install("current", "10.0.112", "11.0.100-rc.1.26425.128");

        Assert.Equal(Path.Combine(current, Executable),
            DotnetLocator.Find(null, 11, Environment(null, old, current)));
        Assert.Equal(Path.Combine(old, Executable), DotnetLocator.Find(null, 10, Environment(null, old, current)));
    }

    /// <summary>A missing required SDK fails with a message naming the version and the folders checked.</summary>
    [Fact]
    public void MissingRequiredSdkFailsClearly()
    {
        var old = Install("old", "10.0.112");

        var error = Assert.Throws<InvalidOperationException>(() => DotnetLocator.Find(null, 11, Environment(null, old)));
        Assert.Contains(".NET 11 SDK", error.Message);
        Assert.Contains(old, error.Message);

        Assert.Contains("No dotnet executable", Assert.Throws<InvalidOperationException>(
            () => DotnetLocator.Find(null, 11, Environment(null))).Message);
    }

    /// <summary>A symlinked executable, as on PATH, is judged by the SDKs beside its real location.</summary>
    [Fact]
    public void SymlinkedExecutablesUseTheirRealInstallation()
    {
        if (OperatingSystem.IsWindows()) return;

        var real = Install("real", "11.0.100");
        var bin = Path.Combine(directory, "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, Executable), Path.Combine(real, Executable));

        Assert.Equal(Path.Combine(bin, Executable), DotnetLocator.Find(null, 11, Environment(null, bin)));
    }
}
