namespace Turian.Tests;

/// <summary>The <c>.deps.json</c> written beside a brick's prebuilt assembly for dependency resolution.</summary>
public sealed class BrickDepsJsonTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-brick-deps-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The document names the runtime target and lists the brick assembly with its restored files.</summary>
    [Fact]
    public void DocumentListsBrickAssemblyAndRestoredFiles()
    {
        var assembly = Path.Combine(root, "Brick.dll");
        File.Copy(typeof(BrickNuGet).Assembly.Location, assembly);
        var files = new[] { new BrickRuntimeFile("X", "1.4.0", "X.dll") };

        BrickDepsJson.Write(assembly, files);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".deps.json")));
        var rootElement = document.RootElement;
        Assert.Equal(".NETCoreApp,Version=v10.0", rootElement.GetProperty("runtimeTarget").GetProperty("name").GetString());
        var targets = rootElement.GetProperty("targets").GetProperty(".NETCoreApp,Version=v10.0");
        Assert.True(targets.TryGetProperty("X/1.4.0", out var package));
        Assert.True(package.GetProperty("runtime").TryGetProperty("X.dll", out _));
        Assert.Equal("project", rootElement.GetProperty("libraries").GetProperty("X/1.4.0").GetProperty("type").GetString());
    }

    /// <summary>The brick assembly's own entry uses its file name.</summary>
    [Fact]
    public void DocumentListsOwnAssembly()
    {
        var assembly = Path.Combine(root, "Brick.dll");
        File.Copy(typeof(BrickNuGet).Assembly.Location, assembly);

        BrickDepsJson.Write(assembly, []);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".deps.json")));
        var targets = document.RootElement.GetProperty("targets").GetProperty(".NETCoreApp,Version=v10.0");
        var own = targets.EnumerateObject().Single(static p => p.Name.StartsWith("Brick/", StringComparison.Ordinal));
        Assert.True(own.Value.GetProperty("runtime").TryGetProperty("Brick.dll", out _));
    }
}
