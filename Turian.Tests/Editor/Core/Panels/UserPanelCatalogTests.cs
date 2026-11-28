namespace Turian.Tests;

/// <summary>
/// Covers the discovery rules behind user-contributed panels: which classes qualify, what their page
/// looks like, and that a type the editor cannot construct is skipped rather than breaking the scan.
/// </summary>
public sealed class UserPanelCatalogTests
{
    /// <summary>A plain panel, the common case.</summary>
    [Panel("Custom Panel", "Tools/Custom Panel")]
    public sealed class Fixture
    {
        /// <summary>A plain member, standing in for whatever a real panel would expose.</summary>
        public string Message { get; set; } = "hi";
    }

    /// <summary>No public parameterless constructor — the editor cannot create it.</summary>
    [Panel("Unusable", "Tools/Unusable")]
    public sealed class UnusableFixture
    {
        /// <summary>Takes a parameter, so <see cref="UserPanelCatalog"/> cannot construct it.</summary>
        public UnusableFixture(int requiredParameter) => RequiredParameter = requiredParameter;

        /// <summary>The value required by this fixture's non-default constructor.</summary>
        public int RequiredParameter { get; }
    }

    /// <summary>No attribute at all.</summary>
    public sealed class IgnoredFixture;

    /// <summary>A panel that records when its catalog releases it.</summary>
    [Panel("Disposable", "Tools/Disposable")]
    public sealed class DisposableFixture : IDisposable
    {
        /// <summary>The number of releases.</summary>
        public int Releases { get; private set; }

        /// <summary>Records the release.</summary>
        public void Dispose() => Releases++;
    }

    /// <summary>Unchanged assemblies retain their panel instances and removed assemblies release them once.</summary>
    [Fact]
    public void RefreshRetainsAndReleasesPanelInstances()
    {
        using var build = new BuildManager(new BuildAppSettings(), NullLogger.Instance);
        using var catalog = new UserPanelCatalog(build, NullLogger.Instance);
        var changes = 0;
        catalog.Changed += () => changes++;
        var assembly = typeof(Fixture).Assembly;

        var target = (DisposableFixture)catalog.Refresh([assembly])
            .Single(static page => page.Path == "Tools/Disposable").Target;
        catalog.Refresh([assembly]);
        Assert.Equal(1, changes);
        Assert.Equal(0, target.Releases);

        catalog.Refresh([]);
        catalog.Refresh([]);
        Assert.Equal(2, changes);
        Assert.Equal(1, target.Releases);
    }

    /// <summary>A panel whose constructor cannot complete.</summary>
    [Panel("Broken constructor", "Tools/Broken constructor")]
    public sealed class BrokenConstructor
    {
        /// <summary>Fails to construct.</summary>
        public BrokenConstructor() => throw new InvalidOperationException();
    }

    /// <summary>A panel whose disposal cannot complete.</summary>
    [Panel("Broken disposal", "Tools/Broken disposal")]
    public sealed class BrokenDisposal : IDisposable
    {
        /// <summary>The number of disposal attempts.</summary>
        public int Attempts { get; private set; }

        /// <summary>Records the attempt and fails.</summary>
        public void Dispose()
        {
            Attempts++;
            throw new InvalidOperationException();
        }
    }

    /// <summary>Failed construction and disposal do not prevent other panels from being released.</summary>
    [Fact]
    public void FailuresAndRepeatedDisposeDoNotInterruptCleanup()
    {
        using var build = new BuildManager(new BuildAppSettings(), NullLogger.Instance);
        var catalog = new UserPanelCatalog(build, NullLogger.Instance);
        var pages = catalog.Refresh([typeof(Fixture).Assembly]);
        var broken = (BrokenDisposal)pages.Single(static page => page.Path == "Tools/Broken disposal").Target;
        var other = (DisposableFixture)pages.Single(static page => page.Path == "Tools/Disposable").Target;
        Assert.DoesNotContain(pages, static page => page.Path == "Tools/Broken constructor");

        catalog.Dispose();
        catalog.Dispose();
        Assert.Equal(1, broken.Attempts);
        Assert.Equal(1, other.Releases);
    }

    /// <summary>Project and editor definitions remain eligible while runtime brick definitions are excluded.</summary>
    [Fact]
    public void SourceBrickFilterRetainsProjectAndEditorDefinitions()
    {
        var assets = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project", "Assets"));
        var definitions = new[]
        {
            new ProjectAssembly("Project", "", [], Path.Combine(assets, "Game", "Assembly.dataasset"), [], false, false, false),
            new ProjectAssembly("Runtime", "", [], Path.Combine(assets, "..", "Bricks", "Runtime.dataasset"), [], false, false, false),
            new ProjectAssembly("Editor", "", [], Path.Combine(assets, "..", "Bricks", "Editor.dataasset"), [], true, false, false),
        };

        Assert.Equal(["Runtime"], BuildManager.RuntimeBrickAssemblyNames(definitions, assets));
    }

    static IReadOnlyList<UserPanelPage> Scan() =>
        UserPanelCatalog.Scan(typeof(Fixture).Assembly, NullLogger.Instance);

    /// <summary>An annotated, constructible class becomes a page; anything else does not.</summary>
    [Fact]
    public void OnlyAnnotatedConstructibleClassesBecomePages()
    {
        var pages = Scan();

        Assert.Contains(pages, static page => page.Path == "Tools/Custom Panel");
        Assert.DoesNotContain(pages, static page => page.Path == "Tools/Unusable");
    }

    /// <summary>The page carries the attribute's name and path, and a freshly built instance.</summary>
    [Fact]
    public void APageCarriesItsNamePathAndInstance()
    {
        var page = Scan().Single(static page => page.Path == "Tools/Custom Panel");

        Assert.Equal("Custom Panel", page.Name);
        Assert.IsType<Fixture>(page.Target);
        Assert.Equal("hi", ((Fixture)page.Target).Message);
    }

    /// <summary>Page ids are stable across scans, so re-registering replaces rather than duplicates.</summary>
    [Fact]
    public void PageIdsAreStableAcrossScans()
    {
        var first = Scan().Single(static page => page.Path == "Tools/Custom Panel").Id;
        var second = Scan().Single(static page => page.Path == "Tools/Custom Panel").Id;

        Assert.Equal(first, second);
    }
}
