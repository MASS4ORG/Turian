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

    static IReadOnlyList<UserPanelPage> Scan() =>
        UserPanelCatalog.Scan(typeof(Fixture).Assembly, NullLogger.Instance);

    /// <summary>An annotated, constructible class becomes a page; anything else does not.</summary>
    [Fact]
    public void OnlyAnnotatedConstructibleClassesBecomePages()
    {
        var pages = Scan();

        Assert.Contains(pages, page => page.Path == "Tools/Custom Panel");
        Assert.DoesNotContain(pages, page => page.Path == "Tools/Unusable");
    }

    /// <summary>The page carries the attribute's name and path, and a freshly built instance.</summary>
    [Fact]
    public void APageCarriesItsNamePathAndInstance()
    {
        var page = Scan().Single(page => page.Path == "Tools/Custom Panel");

        Assert.Equal("Custom Panel", page.Name);
        Assert.IsType<Fixture>(page.Target);
        Assert.Equal("hi", ((Fixture)page.Target).Message);
    }

    /// <summary>Page ids are stable across scans, so re-registering replaces rather than duplicates.</summary>
    [Fact]
    public void PageIdsAreStableAcrossScans()
    {
        var first = Scan().Single(page => page.Path == "Tools/Custom Panel").Id;
        var second = Scan().Single(page => page.Path == "Tools/Custom Panel").Id;

        Assert.Equal(first, second);
    }
}
