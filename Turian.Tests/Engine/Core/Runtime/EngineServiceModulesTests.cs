namespace Turian.Tests;

/// <summary>Exercises package registration, designer-authored rules, and per-session overrides.</summary>
public class EngineServiceModulesTests
{
    [TypeId("bbec2765-77a6-47e9-8bf0-c4155ad3f000")]
    sealed class BagRule : DataAsset
    {
        public int MaxItems { get; set; }
    }

    /// <summary>Package contract replaced by a test double in a test session.</summary>
    public interface IBagManager
    {
        /// <summary>Checks a capacity rule.</summary>
        Task<bool> CanStoreAsync(Guid ruleId, int count);
    }

    sealed class BagManager(IAssetLoader loader) : IBagManager
    {
        public async Task<bool> CanStoreAsync(Guid ruleId, int count) =>
            count < (await loader.LoadContentAsync<BagRule>(ruleId))!.MaxItems;
    }

    sealed class BagComponent : Component
    {
        [InjectService, JsonIgnore]
        public IBagManager? Manager { get; private set; }
    }

    /// <summary>Simulates a package module discovered in a user assembly.</summary>
    public sealed class BagModule : IEngineServiceModule
    {
        /// <inheritdoc />
        public void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<IBagManager, BagManager>();
    }

    /// <summary>Separate sessions can load distinct designer rules through independent asset loaders.</summary>
    [Fact]
    public async Task RegisteredPackage_UsesDataAssetsFromItsOwnSession()
    {
        var smallId = Guid.NewGuid();
        var largeId = Guid.NewGuid();
        var smallLoader = Substitute.For<IAssetLoader>();
        var largeLoader = Substitute.For<IAssetLoader>();
        smallLoader.LoadContentAsync<BagRule>(smallId).Returns(Task.FromResult<BagRule?>(new BagRule { MaxItems = 2 }));
        smallLoader.LoadContentAsync<BagRule>(largeId).Returns(Task.FromResult<BagRule?>(new BagRule { MaxItems = 6 }));
        largeLoader.LoadContentAsync<BagRule>(smallId).Returns(Task.FromResult<BagRule?>(new BagRule { MaxItems = 2 }));
        using var small = new ServiceCollection()
            .AddSingleton(smallLoader)
            .AddEngineModules(typeof(BagModule).Assembly)
            .BuildServiceProvider();
        using var large = new ServiceCollection()
            .AddSingleton(largeLoader)
            .AddEngineModules(typeof(BagModule).Assembly)
            .BuildServiceProvider();

        Assert.False(await small.GetRequiredService<IBagManager>().CanStoreAsync(smallId, 3));
        Assert.True(await small.GetRequiredService<IBagManager>().CanStoreAsync(largeId, 3));
        Assert.False(await large.GetRequiredService<IBagManager>().CanStoreAsync(smallId, 3));
        Assert.NotSame(small.GetRequiredService<IBagManager>(), large.GetRequiredService<IBagManager>());
    }

    /// <summary>Tests can replace a package service without changing a global or another scene.</summary>
    [Fact]
    public void TestSession_OverridesPackageRegistrationBeforeSceneAwake()
    {
        var replacement = Substitute.For<IBagManager>();
        using var provider = new ServiceCollection()
            .AddEngineModules(typeof(BagModule).Assembly)
            .AddSingleton(replacement)
            .BuildServiceProvider();
        var component = new BagComponent();
        var root = new Node();
        root.Components.Add(component);

        root.Awake(null, provider);

        Assert.Same(replacement, component.Manager);
    }

    /// <summary>An assembly listed twice in a resolved graph registers its modules only once.</summary>
    [Fact]
    public void GraphRegistration_DeduplicatesAssemblies()
    {
        var assembly = typeof(BagModule).Assembly;
        var services = new ServiceCollection().AddEngineModules(assembly, assembly);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IBagManager));
    }
}
