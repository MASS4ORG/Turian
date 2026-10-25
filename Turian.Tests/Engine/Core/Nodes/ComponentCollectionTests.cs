namespace Turian.Tests;

/// <summary>Checks the owned components collection, its notifications and component destruction.</summary>
[Collection(SerialTests.Name)]
public class ComponentCollectionTests
{
    sealed class Plain : Component;

    sealed class Lifecycle : Component
    {
        public List<string> Events { get; } = [];
        public int Updates { get; private set; }

        public override void OnUpdate(float deltaTime) => Updates++;
        public override void OnDestroy() => Events.Add("destroy");
    }

    sealed class DisposableComponent : Component, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    sealed class DestroyingComponent : Component
    {
        public Component? Target { get; init; }
        public bool InFixedUpdate { get; init; }

        public override void OnUpdate(float deltaTime)
        {
            if (!InFixedUpdate) { Target?.Destroy(); }
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            if (InFixedUpdate) { Target?.Destroy(); }
        }
    }

    /// <summary>Every structural operation advances the version and notifies once; replacement coalesces.</summary>
    [Fact]
    public void EditsNotifyOncePerOperation()
    {
        var node = new Node();
        var components = node.Components;
        var notifications = 0;
        components.StructureChanged += (_, args) =>
        {
            Assert.Same(EventArgs.Empty, args);
            notifications++;
        };
        Component a = new Plain(), b = new Plain(), c = new Plain();

        components.Add(a);
        components.Insert(0, b);
        components.Add(c);
        components.Move(2, 0);
        Assert.Equal([c, b, a], components);
        components.RemoveAt(1);
        Assert.True(components.Remove(a));
        Assert.False(components.Remove(a));
        Assert.Equal(6, notifications);

        var version = components.Version;
        node.Components = [a, b, a, c];
        Assert.Equal([a, b, c], components);
        Assert.Equal(7, notifications);
        Assert.NotEqual(version, components.Version);
        components.Clear();
        components.Clear();
        Assert.Empty(components);
        Assert.Equal(8, notifications);
    }

    /// <summary>A component is listed by one node: listing it elsewhere moves it, and repeated adds move it to the end.</summary>
    [Fact]
    public void OwnedListingIsExclusive()
    {
        Node first = new(), second = new();
        Component a = new Plain(), b = new Plain();
        first.Components.Add(a);
        first.Components.Add(b);
        var firstNotifications = 0;
        first.Components.StructureChanged += (_, _) => firstNotifications++;

        first.Components.Add(a);
        Assert.Equal([b, a], first.Components);
        first.Components.Add(a);
        second.Components.Insert(0, a);

        Assert.Equal([b], first.Components);
        Assert.Equal([a], second.Components);
        Assert.Equal(2, firstNotifications);
        Assert.DoesNotContain(a, first.Components);
        Assert.Equal(-1, first.Components.IndexOf(a));
        Assert.Throws<ArgumentOutOfRangeException>(() => second.Components.Insert(1, a));

        second.Components[0] = b;
        Assert.Empty(first.Components);
        Assert.Equal([b], second.Components);
        Assert.DoesNotContain(a, second.Components);
    }

    /// <summary>Membership compares references, because components with one id still differ as objects.</summary>
    [Fact]
    public void MembershipUsesReferenceIdentity()
    {
        var node = new Node();
        var listed = new Plain();
        var twin = new Plain { Id = listed.Id };
        node.Components.Add(listed);

        // Assert.Contains would compare ids, which the twin shares.
        var listedFound = node.Components.Contains(listed);
        var twinFound = node.Components.Contains(twin);
        Assert.True(listedFound);
        Assert.False(twinFound);
        Assert.False(node.Components.Remove(twin));
        Assert.Equal(-1, node.Components.IndexOf(null!));
        Assert.Throws<ArgumentNullException>(() => node.Components.Add(null!));
        var copy = new Component[2];
        node.Components.CopyTo(copy, 1);
        Assert.Same(listed, copy[1]);
        Assert.False(node.Components.IsReadOnly);
    }

    /// <summary>The enumerator walks in order without allocating and rejects structural edits.</summary>
    [Fact]
    public void EnumerationRejectsEdits()
    {
        var node = new Node();
        node.Components.Add(new Plain());
        node.Components.Add(new Plain());

        IEnumerable<Component> sequence = node.Components;
        var counted = 0;
        foreach (var _ in sequence) { counted++; }
        Assert.Equal(2, counted);
        System.Collections.IEnumerator untyped = node.Components.GetEnumerator();
        Assert.Throws<NotSupportedException>(untyped.Reset);
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var component in node.Components)
            {
                node.Components.Remove(component);
            }
        });
    }

    /// <summary>An unowned builder keeps a plain list until it is assigned to a node.</summary>
    [Fact]
    public void BuilderCopiesIntoOwnedStorage()
    {
        var a = new Plain();
        var builder = new ComponentCollection { a, a };
        Assert.Equal(2, builder.Count);
        var node = new Node { Components = builder };

        Assert.NotSame(builder, node.Components);
        Assert.Equal([a], node.Components);
        node.Components = node.Components;
        Assert.Equal([a], node.Components);
    }

    /// <summary>
    /// Destroy is deferred like node destruction: the component keeps updating until the frame boundary, then it is
    /// detached, destroyed, disposed and removed. A request from a fixed update flushes with its tick.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestroyIsDeferredToTheBoundary(bool fromFixedUpdate)
    {
        var root = new Node();
        var target = new Lifecycle();
        var disposable = new DisposableComponent();
        var destroyer = new DestroyingComponent { Target = target, InFixedUpdate = fromFixedUpdate };
        root.Components.Add(destroyer);
        root.Components.Add(target);
        var other = new Node();
        other.Components.Add(disposable);
        root.Children.Add(other);
        root.Awake(null);
        var manager = new SceneManager(new AssetDatabase());
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);

        ticker.StepFrame();

        Assert.Equal(fromFixedUpdate ? 0 : 1, target.Updates);
        Assert.True(target.DestroyQueued);
        Assert.True(target.IsDestroyed);
        Assert.Null(target.Node);
        Assert.Equal(["destroy"], target.Events);
        Assert.Equal([destroyer], root.Components);

        disposable.Destroy();
        Assert.Same(other, disposable.Node);
        disposable.Destroy();
        ticker.StepFrame();
        Assert.True(disposable.Disposed);
        Assert.Empty(other.Components);
        Assert.Equal(fromFixedUpdate ? 0 : 1, target.Updates);
    }

    sealed class ThrowingDestroy : Component
    {
        public override void OnDestroy() => throw new InvalidOperationException("destroy failed");
    }

    /// <summary>A component whose OnDestroy throws is still removed, and the rest of the flush runs.</summary>
    [Fact]
    public void ThrowingDestroyDoesNotStopTheFlush()
    {
        var root = new Node();
        var throwing = new ThrowingDestroy();
        var after = new Lifecycle();
        root.Components.Add(throwing);
        root.Components.Add(after);
        root.Awake(null);
        var manager = new SceneManager(new AssetDatabase());
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);

        throwing.Destroy();
        after.Destroy();
        ticker.StepFrame();

        Assert.Empty(root.Components);
        Assert.True(after.IsDestroyed);
    }

    /// <summary>DestroyImmediate removes now; destroying an unattached component only marks it destroyed.</summary>
    [Fact]
    public void DestroyImmediateRemovesNow()
    {
        var node = new Node();
        var component = new Lifecycle();
        node.AddComponent(component);

        component.DestroyImmediate();
        component.DestroyImmediate();

        Assert.Empty(node.Components);
        Assert.True(component.IsDestroyed);
        Assert.Equal(["destroy"], component.Events);

        var loose = new Plain();
        loose.Destroy();
        Assert.True(loose.IsDestroyed);
        Assert.True(loose.DestroyQueued);
        loose.Destroy();
    }

    /// <summary>TryGetComponent finds the first matching component or reports none.</summary>
    [Fact]
    public void TryGetComponentReportsTheFirstMatch()
    {
        var node = new Node();
        var first = new Plain();
        node.Components.Add(new Lifecycle());
        node.Components.Add(first);
        node.Components.Add(new Plain());

        Assert.True(node.TryGetComponent<Plain>(out var found));
        Assert.Same(first, found);
        Assert.False(node.TryGetComponent<DisposableComponent>(out var missing));
        Assert.Null(missing);
    }
}
