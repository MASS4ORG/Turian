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

    [TypeId("a4000044-0000-4000-8000-000000000003")]
    sealed class OptionalComponent : Component
    {
        [InjectService(Optional = true), JsonIgnore]
        public IInputSource? Input { get; private set; }
    }

    [TypeId("a4000044-0000-4000-8000-000000000004")]
    sealed class PreviewComponent : Component
    {
        [InjectService, JsonIgnore]
        public ISceneManager? SceneManager { get; private set; }
    }

    /// <summary>Scene and component services are ready before lifecycle callbacks.</summary>
    [Fact]
    public void Awake_InjectsSceneServicesBeforeCallbacksAndIntoChildren()
    {
        var scene = Substitute.For<ISceneManager>();
        var input = Substitute.For<IInputSource>();
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
        var first = Substitute.For<IInputSource>();
        var second = Substitute.For<IInputSource>();
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

    /// <summary>Editor previews may omit optional runtime-only services.</summary>
    [Fact]
    public void Awake_OptionalServiceIsNullWithoutRegistration()
    {
        using var preview = new ServiceCollection().BuildServiceProvider();
        var node = new Node();
        var component = new OptionalComponent();
        node.Components.Add(component);

        node.Awake(null, preview);

        Assert.Null(component.Input);
    }

    /// <summary>Edit-time preview may omit game services, while play remains strict by default.</summary>
    [Fact]
    public void Awake_EditPreviewAllowsMissingGameplayServices()
    {
        using var editorServices = new ServiceCollection()
            .AddSingleton(Substitute.For<IInputSource>())
            .BuildServiceProvider();
        var component = new PreviewComponent();
        var preview = new Node();
        preview.Components.Add(component);

        preview.Awake(null, editorServices, allowMissingServices: true);

        Assert.Null(component.SceneManager);
        var play = new Node();
        play.Components.Add(new PreviewComponent());
        Assert.Throws<InvalidOperationException>(() => play.Awake(null, editorServices));
    }
}
