namespace Turian.Tests;

/// <summary>
/// Covers the discovery rules behind user-contributed main-menu items: which methods qualify, how a
/// path maps onto a menu, and what happens to a signature the editor cannot call.
/// </summary>
public sealed class UserMenuCatalogTests
{
    /// <summary>Methods this assembly exposes to the scanner, standing in for user code.</summary>
    public static class Fixtures
    {
        /// <summary>Records what the scanned methods did, so a test can assert they ran.</summary>
        public static readonly List<string> Calls = [];

        /// <summary>A plain parameterless entry, the common case.</summary>
        [MenuItem("Tools/Rebuild")]
        public static void Rebuild() => Calls.Add("rebuild");

        /// <summary>An entry nested two levels below its menu.</summary>
        [MenuItem("Tools/Level/Bake Lighting")]
        public static void Bake() => Calls.Add("bake");

        /// <summary>The catch-all parameter the Avalonia studio used; it receives the provider.</summary>
        [MenuItem("Tools/WithContext")]
        public static void WithContext(object? context) => Calls.Add($"context:{context is not null}");

        /// <summary>Too many parameters for the editor to supply — skipped.</summary>
        [MenuItem("Tools/Unusable")]
        public static void Unusable(int a, int b) => Calls.Add("unusable");

        /// <summary>No attribute at all.</summary>
        public static void Ignored() => Calls.Add("ignored");
    }

    /// <summary>Holds the instance method, which a static class cannot.</summary>
    public sealed class InstanceFixture
    {
        /// <summary>Not static, so it is never a menu item.</summary>
        [MenuItem("Tools/Instance")]
        public void Instance()
        {
        }
    }

    static IReadOnlyList<UserMenuCommand> Scan() =>
        UserMenuCatalog.Scan(typeof(Fixtures).Assembly, NullLogger.Instance);

    static UserMenuCommand Find(string path) =>
        Scan().Single(command => command.Path == path);

    /// <summary>A public static method with a path becomes an entry; anything else does not.</summary>
    [Fact]
    public void OnlyAnnotatedPublicStaticMethodsBecomeEntries()
    {
        var paths = Scan().Select(command => command.Path).ToList();

        Assert.Contains("Tools/Rebuild", paths);
        Assert.Contains("Tools/Level/Bake Lighting", paths);
        Assert.DoesNotContain("Tools/Instance", paths);
        Assert.DoesNotContain("Tools/Ignored", paths);
    }

    /// <summary>A method the editor cannot call is dropped rather than breaking the scan.</summary>
    [Fact]
    public void AMethodWithTooManyParametersIsSkipped()
    {
        Assert.DoesNotContain("Tools/Unusable", Scan().Select(command => command.Path));
    }

    /// <summary>The first path segment is the menu, the last is the label, the middle is the submenu.</summary>
    [Fact]
    public void APathSplitsIntoMenuSubmenuAndLabel()
    {
        var flat = Find("Tools/Rebuild");
        Assert.Equal("Tools", flat.Menu);
        Assert.Equal("Rebuild", flat.Label);
        Assert.Equal(string.Empty, flat.SubmenuPath);

        var nested = Find("Tools/Level/Bake Lighting");
        Assert.Equal("Tools", nested.Menu);
        Assert.Equal("Bake Lighting", nested.Label);
        Assert.Equal("Level", nested.SubmenuPath);
    }

    /// <summary>Entry ids are stable across scans, so re-registering replaces rather than duplicates.</summary>
    [Fact]
    public void EntryIdsAreStableAcrossScans()
    {
        Assert.Equal(Find("Tools/Rebuild").Id, Find("Tools/Rebuild").Id);
        Assert.NotEqual(Find("Tools/Rebuild").Id, Find("Tools/WithContext").Id);
    }

    /// <summary>Invoking an entry runs the method, and an <c>object</c> parameter receives the provider.</summary>
    [Fact]
    public void InvokingAnEntryRunsTheMethod()
    {
        Fixtures.Calls.Clear();
        var services = new ServiceCollection().BuildServiceProvider();

        Find("Tools/Rebuild").Invoke(services);
        Find("Tools/WithContext").Invoke(services);

        Assert.Equal(["rebuild", "context:True"], Fixtures.Calls);
    }
}
