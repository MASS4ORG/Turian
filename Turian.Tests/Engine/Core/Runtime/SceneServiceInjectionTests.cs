namespace Turian.Tests;

/// <summary>Verifies scene-bound injection independently of process-wide runtime services.</summary>
public class SceneServiceInjectionTests
{
    [TypeId("a4000044-0000-4000-8000-000000000001")]
    sealed class InjectedNode : Node
    {
        [InjectService, JsonIgnore]
        public ISceneManager? SceneManager { get; private set; }

    }

    [TypeId("a4000044-0000-4000-8000-000000000002")]
    sealed class InjectedComponent : Component
    {
        [InjectService, JsonIgnore]
        public IInputSource? Input { get; private set; }

        [JsonIgnore]
        public IInputSource? InputAtAwake { get; private set; }

        public override void OnAwake() => InputAtAwake = Input;
    }

    /// <summary>Scene and component services are ready before lifecycle callbacks.</summary>
    [Fact]
    public void Awake_InjectsSceneServicesBeforeCallbacksAndIntoChildren()
    {
        var scene = NSubstitute.Substitute.For<ISceneManager>();
        var input = NSubstitute.Substitute.For<IInputSource>();
        using var provider = new ServiceCollection()
            .AddSingleton(scene)
            .AddSingleton(input)
            .BuildServiceProvider();
        var root = new InjectedNode();
        var child = new Node();
        var component = new InjectedComponent();
        child.Components.Add(component);
        root.Children.Add(child);

        root.Awake(null, provider);

        Assert.Same(scene, root.SceneManager);
        Assert.Same(input, component.InputAtAwake);
        Assert.Same(provider, child.Services);
        Assert.DoesNotContain("Input", Serializer.Serialize(root));
    }

    /// <summary>Dynamically added components use their node's provider.</summary>
    [Fact]
    public void AddComponent_UsesTheNodeProviderRatherThanTheGlobalProvider()
    {
        var first = NSubstitute.Substitute.For<IInputSource>();
        var second = NSubstitute.Substitute.For<IInputSource>();
        using var firstProvider = new ServiceCollection().AddSingleton(first).BuildServiceProvider();
        using var secondProvider = new ServiceCollection().AddSingleton(second).BuildServiceProvider();
        var firstNode = new Node();
        var secondNode = new Node();
        firstNode.Awake(null, firstProvider);
        secondNode.Awake(null, secondProvider);

        var firstComponent = firstNode.AddComponent(new InjectedComponent());
        var secondComponent = secondNode.AddComponent(new InjectedComponent());

        Assert.Same(first, ((InjectedComponent)firstComponent).InputAtAwake);
        Assert.Same(second, ((InjectedComponent)secondComponent).InputAtAwake);
    }

    /// <summary>Missing registrations fail fast instead of silently using a global fallback.</summary>
    [Fact]
    public void Awake_WithMissingRequiredService_FailsBeforeCallbacks()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var root = new Node();
        root.Components.Add(new InjectedComponent());

        Assert.Throws<InvalidOperationException>(() => root.Awake(null, provider));
    }
}
