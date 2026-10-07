namespace Turian.Tests;

/// <summary>Checks that built-in and discovered settings use the same attribute-only page configuration.</summary>
public sealed class SettingsMetadataTests
{
    /// <summary>A project-scoped page with explicit identity and presentation metadata.</summary>
    [Gaya.EditorSetting(" Custom/Tools ", Id = " custom.tools ", Description = "Tool preferences", Order = -3,
        Workspace = true, Hidden = true)]
    public sealed class CustomPage
    {
        /// <summary>A persisted option with a declared default.</summary>
        public int Count { get; set; } = 7;
    }

    /// <summary>A user page whose storage identity derives from its type.</summary>
    [Gaya.EditorSetting("User/Automatic")]
    public sealed class AutomaticPage;

    /// <summary>A class with missing page configuration.</summary>
    public sealed class UnannotatedPage;

    /// <summary>A class with an invalid category path.</summary>
    [Gaya.EditorSetting(" ")]
    public sealed class EmptyPathPage;

    /// <summary>All metadata is read from the class, including the storage scope and hidden state.</summary>
    [Fact]
    public void PageConfigurationComesFromItsAttribute()
    {
        var target = new CustomPage();
        var page = new SettingsPageDescriptor(target);
        Assert.Same(target, page.Target);
        Assert.Equal("custom.tools", page.Id);
        Assert.Equal("Custom/Tools", page.Path);
        Assert.Equal("Tools", page.Title);
        Assert.Equal("Tool preferences", page.Description);
        Assert.Equal(-3, page.Order);
        Assert.Equal(SettingsScope.Workspace, page.Scope);
        Assert.True(page.Hidden);
        target.Count = 19;
        Assert.Equal(7, page.Defaults[nameof(CustomPage.Count)]);
    }

    /// <summary>Discovery and registration preserve matching explicit and derived storage ids.</summary>
    [Fact]
    public void UserAndBuiltInRegistrationReadTheSameMetadata()
    {
        var pages = UserSettingsCatalog.Scan(typeof(CustomPage).Assembly, NullLogger.Instance);
        var discovered = Assert.Single(pages, page => page.Target is CustomPage);
        var descriptor = new SettingsPageDescriptor(discovered.Target);
        Assert.Equal(descriptor.Id, discovered.Id);
        Assert.Equal(descriptor.Path, discovered.Path);
        Assert.Equal(descriptor.Description, discovered.Description);
        Assert.Equal(descriptor.Order, discovered.Order);
        Assert.True(discovered.Workspace);
        var automatic = Assert.Single(pages, page => page.Target is AutomaticPage);
        Assert.Equal($"usercode.settings.{typeof(AutomaticPage).FullName}", automatic.Id);
        Assert.Equal(automatic.Id, new SettingsPageDescriptor(automatic.Target).Id);
        Assert.Equal(SettingsScope.User, new SettingsPageDescriptor(automatic.Target).Scope);
        Assert.DoesNotContain(pages, page => page.Target is EmptyPathPage or UnannotatedPage);
    }

    /// <summary>Discovery retains available settings types and contains assembly inspection failures.</summary>
    [Fact]
    public void DiscoveryContainsAssemblyLoadFailures()
    {
        var assembly = Substitute.For<Assembly>();
        assembly.GetTypes().Returns(_ => throw new ReflectionTypeLoadException(
            [typeof(AutomaticPage), null], [new TypeLoadException("Unavailable project dependency")]));

        var page = Assert.Single(UserSettingsCatalog.Scan(assembly, NullLogger.Instance));

        Assert.IsType<AutomaticPage>(page.Target);
        Assert.Equal("User/Automatic", page.Path);
        var unavailable = Substitute.For<Assembly>();
        unavailable.GetName().Returns(new AssemblyName("Unavailable"));
        unavailable.GetTypes().Returns(_ => throw new NotSupportedException("Unavailable assembly metadata"));
        Assert.Empty(UserSettingsCatalog.Scan(unavailable, NullLogger.Instance));
    }

    /// <summary>Registration rejects objects that do not declare a usable settings page.</summary>
    [Fact]
    public void InvalidSettingsCannotEnterTheRegistry()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsPageDescriptor(null!));
        Assert.Throws<ArgumentException>(() => new SettingsPageDescriptor(new UnannotatedPage()));
        Assert.Throws<ArgumentException>(() => new SettingsPageDescriptor(new EmptyPathPage()));
        Assert.Throws<ArgumentNullException>(() => new Gaya.EditorSettingAttribute().IdFor(null!));
    }
}
