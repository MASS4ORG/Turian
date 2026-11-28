namespace Turian.Tests;

/// <summary>A brick is consumed as precast only when everything it depends on is, so it never loads before a dependency.</summary>
public sealed class PrecastDependencyRuleTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-precast-rule-").FullName;
    readonly List<string> warnings = [];
    readonly List<string> errors = [];

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A precast brick whose dependency has only sources compiles from its own sources instead, with one warning.</summary>
    [Fact]
    public void PrecastBrickWithSourceOnlyDependencyFallsBackToItsSources()
    {
        var dependency = Brick("dep.source", sources: true);
        var brick = Brick("top.precast", sources: true, precast: true, dependsOn: "dep.source");

        BrickAssemblies.ApplyDependencyRule([dependency, brick], Logger());

        Assert.False(BrickAssemblies.IsPrecast(brick));
        Assert.Empty(BrickAssemblies.RuntimeAssemblies(brick));
        var warning = Assert.Single(warnings);
        Assert.Contains("top.precast", warning);
        Assert.Contains("dep.source", warning);
        Assert.Empty(errors);
    }

    /// <summary>A brick that ships no sources cannot fall back, so it is reported as unusable and nothing of it loads.</summary>
    [Fact]
    public void PrecastBrickWithoutSourcesIsUnusable()
    {
        var dependency = Brick("dep.source", sources: true);
        var brick = Brick("top.binary", sources: false, precast: true, dependsOn: "dep.source");

        BrickAssemblies.ApplyDependencyRule([dependency, brick], Logger());

        Assert.False(BrickAssemblies.IsPrecast(brick));
        Assert.Empty(BrickAssemblies.EditorAssemblies(brick));
        Assert.Contains("unusable", Assert.Single(errors));
        Assert.Empty(warnings);
    }

    /// <summary>The rule follows dependencies transitively, so a brick above a fallen-back one falls back too.</summary>
    [Fact]
    public void FallbackPropagatesThroughPrecastDependencies()
    {
        var source = Brick("dep.source", sources: true);
        var middle = Brick("mid.precast", sources: true, precast: true, dependsOn: "dep.source");
        var top = Brick("top.precast", sources: true, precast: true, dependsOn: "mid.precast");

        BrickAssemblies.ApplyDependencyRule([source, middle, top], Logger());

        Assert.False(BrickAssemblies.IsPrecast(middle));
        Assert.False(BrickAssemblies.IsPrecast(top));
        Assert.Equal(2, warnings.Count);
    }

    /// <summary>Precast, built-in and code-free dependencies leave the brick precast, and a decision is replaced when the set changes.</summary>
    [Fact]
    public void LoadableDependenciesKeepTheBrickPrecast()
    {
        var precast = Brick("dep.precast", sources: true, precast: true);
        var builtin = Brick("dep.builtin", sources: true, origin: PackageOrigin.Builtin);
        var content = Brick("dep.content", sources: false);
        var brick = Brick("top.precast", sources: true, precast: true, dependsOn: ["dep.precast", "dep.builtin", "dep.content"]);

        BrickAssemblies.ApplyDependencyRule([precast, builtin, content, brick], Logger());

        Assert.True(BrickAssemblies.IsPrecast(brick));
        Assert.NotEmpty(BrickAssemblies.RuntimeAssemblies(brick));
        Assert.Empty(warnings);

        var source = Brick("dep.source", sources: true);
        var changed = Brick("again.precast", sources: true, precast: true, dependsOn: "dep.source");
        BrickAssemblies.ApplyDependencyRule([source, changed], Logger());
        BrickAssemblies.ApplyDependencyRule([source, Brick("again.precast", sources: true, precast: true)], Logger());
        Assert.True(BrickAssemblies.IsPrecast(changed));
    }

    ResolvedPackage Brick(string id, bool sources, bool precast = false, string? dependsOn = null,
        PackageOrigin origin = PackageOrigin.Link) =>
        Brick(id, sources, precast, dependsOn is null ? [] : [dependsOn], origin);

    ResolvedPackage Brick(string id, bool sources, bool precast, string[] dependsOn, PackageOrigin origin = PackageOrigin.Link)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        if (sources)
        {
            File.WriteAllText(Path.Combine(folder, "Code.cs"), "class Code {}");
        }

        if (precast)
        {
            var lib = Directory.CreateDirectory(Path.Combine(folder, BrickAssemblies.PrecastFolder, "lib")).FullName;
            File.WriteAllBytes(Path.Combine(lib, $"{id}.dll"), []);
        }

        var manifest = new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("1.0.0"),
            Dependencies = dependsOn.ToDictionary(static d => d, static _ => "^1.0.0"),
        };
        return new ResolvedPackage(id, manifest, folder, origin, $"file:{folder}", null, null, 1, false);
    }

    CollectingLogger Logger() => new(warnings, errors);

    sealed class CollectingLogger(List<string> warnings, List<string> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var target = logLevel == LogLevel.Error ? errors : logLevel == LogLevel.Warning ? warnings : null;
            target?.Add(formatter(state, exception));
        }
    }
}
