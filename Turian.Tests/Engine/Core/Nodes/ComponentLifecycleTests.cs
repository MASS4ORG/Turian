namespace Turian.Tests;

/// <summary>
/// Tests for the Component lifecycle and related metadata attributes.
/// </summary>
public class ComponentLifecycleTests
{
    [TypeId("a3000004-0000-4000-8000-000000000001")]
    class MockComponent : Component
    {
        public int AwakeCount { get; private set; }
        public int EnableCount { get; private set; }
        public int StartCount { get; private set; }
        public int DisableCount { get; private set; }
        public int DestroyCount { get; private set; }
        public int AttachedCount { get; private set; }
        public int DetachedCount { get; private set; }

        public override void OnAwake() => AwakeCount++;
        public override void OnEnable() => EnableCount++;
        public override void OnStart() => StartCount++;
        public override void OnDisable() => DisableCount++;
        public override void OnDestroy() => DestroyCount++;
        public override void OnAttached() => AttachedCount++;
        public override void OnDetached() => DetachedCount++;
    }

    /// <summary>
    /// Verifies the normal sequence of lifecycle events for a component.
    /// </summary>
    [Fact]
    public void LifecycleSequence_NormalFlow()
    {
        var node = new Node();
        var comp = new MockComponent();

        Assert.Equal(0, comp.AwakeCount);

        comp.Setup(node);

        Assert.Equal(1, comp.AttachedCount);
        Assert.Equal(1, comp.AwakeCount);
        Assert.Equal(1, comp.EnableCount);
        Assert.Equal(0, comp.StartCount);

        comp.EnsureStarted();
        Assert.Equal(1, comp.StartCount);

        comp.IsActive = false;
        Assert.Equal(1, comp.DisableCount);

        comp.IsActive = true;
        Assert.Equal(2, comp.EnableCount);

        comp.Detach();
        Assert.Equal(2, comp.DisableCount);
        Assert.Equal(1, comp.DetachedCount);
        Assert.Equal(1, comp.DestroyCount);
    }

    /// <summary>
    /// Verifies that OnEnable is not called if the component is inactive during setup.
    /// </summary>
    [Fact]
    public void Lifecycle_InactiveOnSetup()
    {
        var node = new Node();
        var comp = new MockComponent { IsActive = false };

        comp.Setup(node);

        Assert.Equal(1, comp.AttachedCount);
        Assert.Equal(1, comp.AwakeCount);
        Assert.Equal(0, comp.EnableCount);
    }

    [TypeId("a3000004-0000-4000-8000-000000000002")]
    [RequireComponent(typeof(RequiredMock))]
    class RequiringMock : Component { }

    [TypeId("a3000004-0000-4000-8000-000000000003")]
    class RequiredMock : Component { }

    /// <summary>
    /// Verifies that Component.GetRequiredComponents correctly identifies required component types.
    /// </summary>
    [Fact]
    public void Component_GetRequiredComponents()
    {
        var required = Component.GetRequiredComponents(typeof(RequiringMock)).ToList();
        Assert.Single(required);
        Assert.Equal(typeof(RequiredMock), required[0]);
    }

    [TypeId("a3000004-0000-4000-8000-000000000004")]
    [DisallowMultipleComponent]
    class SingleOnlyMock : Component { }

    /// <summary>
    /// Verifies that Component.AllowsMultiple correctly respects the DisallowMultipleComponent attribute.
    /// </summary>
    [Fact]
    public void Component_AllowsMultiple()
    {
        Assert.True(Component.AllowsMultiple(typeof(MockComponent)));
        Assert.False(Component.AllowsMultiple(typeof(SingleOnlyMock)));
    }
}
