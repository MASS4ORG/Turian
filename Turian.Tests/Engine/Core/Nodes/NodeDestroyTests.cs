using System.Collections.Specialized;

namespace Turian.Tests;

/// <summary>Deferred node destruction through <see cref="Node.Destroy"/> and its flush points in the game loop.</summary>
public class NodeDestroyTests
{
    readonly AssetDatabase assetDatabase = new();

    /// <summary>A node that records the order of its destruction callbacks and can destroy others from them.</summary>
    sealed class TrackedNode(List<string> log) : Node
    {
        public Node? DestroysOnDestroy { get; init; }

        public bool Throws { get; init; }

        public override void OnDestroy()
        {
            log.Add(Name);
            DestroysOnDestroy?.Destroy();
            if (Throws)
            {
                throw new InvalidOperationException("destroy failed");
            }
        }
    }

    sealed class Counter : Component
    {
        public int Fixed { get; private set; }
        public int Updates { get; private set; }

        public override void OnFixedUpdate(float fixedDeltaTime) => Fixed++;
        public override void OnUpdate(float deltaTime) => Updates++;
    }

    sealed class Destroyer : Component
    {
        public List<Node> FromUpdate { get; init; } = [];
        public List<Node> FromFixedUpdate { get; init; } = [];

        public override void OnFixedUpdate(float fixedDeltaTime) => DestroyAll(FromFixedUpdate);
        public override void OnUpdate(float deltaTime) => DestroyAll(FromUpdate);

        static void DestroyAll(List<Node> nodes)
        {
            foreach (var node in nodes)
            {
                node.Destroy();
            }

            nodes.Clear();
        }
    }

    SceneTicker Play(Node root)
    {
        root.Awake(null);
        var manager = new SceneManager(assetDatabase);
        manager.AdoptScene(Guid.NewGuid(), root);
        return new SceneTicker(manager);
    }

    /// <summary>
    /// Destroying many siblings from an update leaves them in place for the frame, then tears them down in request order
    /// and removes them with one reset rather than one notification per child.
    /// </summary>
    [Fact]
    public void Destroy_FromUpdateFlushesAtFrameEndWithOneReset()
    {
        var log = new List<string>();
        var group = new Node();
        var nodes = Enumerable.Range(0, 100).Select(i => new TrackedNode(log) { Name = $"n{i}" }).ToArray();
        foreach (var node in nodes)
        {
            group.Children.Add(node);
        }

        var keep = new Node();
        group.Children.Insert(50, keep);
        var destroyer = new Destroyer { FromUpdate = [.. nodes.Reverse()] };
        var root = new Node { Children = [group] };
        root.Components.Add(destroyer);
        var ticker = Play(root);
        var actions = new List<NotifyCollectionChangedAction>();
        group.Children.CollectionChanged += (_, args) => actions.Add(args.Action);

        ticker.StepFrame();

        Assert.Equal([keep], group.Children);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(nodes.Reverse().Select(static node => node.Name), log);
        Assert.All(nodes, static node =>
        {
            Assert.True(node.IsDestroyed);
            Assert.Null(node.Parent);
        });
        Assert.Same(group, keep.Parent);
    }

    /// <summary>
    /// A destruction requested in a fixed update applies at the end of that tick, so later catch-up ticks of the same
    /// frame no longer run the node, however many ticks the frame needs.
    /// </summary>
    [Fact]
    public void Destroy_FromFixedUpdateFlushesAtTheTickBoundary()
    {
        var target = new Node();
        var counter = new Counter();
        target.Components.Add(counter);
        var root = new Node { Children = [target] };
        root.Components.Add(new Destroyer { FromFixedUpdate = [target] });
        var ticker = Play(root);

        ticker.Tick(SceneTicker.FixedTimestep * 3.5);

        Assert.Equal(1, counter.Fixed);
        Assert.Equal(0, counter.Updates);
        Assert.Empty(root.Children);
        Assert.True(target.IsDestroyed);
    }

    /// <summary>
    /// A request made in a detached hierarchy follows its root into a scene; destruction callbacks may destroy more
    /// nodes in the same flush, and repeated or nested requests destroy each node once.
    /// </summary>
    [Fact]
    public void Destroy_FollowsReparentingAndCascadesOnce()
    {
        var log = new List<string>();
        var cascaded = new TrackedNode(log) { Name = "cascaded" };
        var child = new TrackedNode(log) { Name = "child" };
        var first = new TrackedNode(log) { Name = "first", DestroysOnDestroy = cascaded, Children = [child] };
        var detached = new Node { Children = [first] };
        var root = new Node { Children = [cascaded] };
        var ticker = Play(root);

        child.Destroy();
        first.Destroy();
        first.Destroy();
        root.Children.Add(detached);
        ticker.StepFrame();

        Assert.Equal(["child", "first", "cascaded"], log);
        Assert.Empty(detached.Children);
        Assert.Equal([detached], root.Children);
        Assert.True(cascaded.IsDestroyed);
        cascaded.Destroy();
        ticker.StepFrame();
        Assert.Equal(3, log.Count);
    }

    /// <summary>A throwing destruction callback is reported and the node still leaves; later requests still run.</summary>
    [Fact]
    public void Destroy_SurvivesAThrowingCallback()
    {
        var log = new List<string>();
        var failing = new TrackedNode(log) { Name = "failing", Throws = true };
        var next = new TrackedNode(log) { Name = "next" };
        var root = new Node { Children = [failing, next] };
        var ticker = Play(root);

        failing.Destroy();
        next.Destroy();
        ticker.StepFrame();

        Assert.Equal(["failing", "next"], log);
        Assert.Empty(root.Children);
        Assert.True(failing.IsDestroyed);
    }

    /// <summary>Immediate destruction applies now, and a pending request for the same node is then skipped.</summary>
    [Fact]
    public void DestroyImmediate_RemovesNow()
    {
        var log = new List<string>();
        var node = new TrackedNode(log) { Name = "now" };
        var root = new Node { Children = [node] };
        var ticker = Play(root);

        node.Destroy();
        node.DestroyImmediate();
        node.DestroyImmediate();
        Assert.Empty(root.Children);
        ticker.StepFrame();

        Assert.Equal(["now"], log);
        Assert.True(node.DestroyQueued);
    }

    /// <summary>A children collection swapped in without the node's own type still drops flushed destructions.</summary>
    [Fact]
    public void Destroy_CompactsAForeignChildrenCollection()
    {
        Node kept = new(), destroyed = new();
        var root = new Node();
        var backing = typeof(Node).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(ObservableCollection<Node>));
        backing.SetValue(root, new ObservableCollection<Node> { destroyed, kept });
        destroyed.Parent = root;
        kept.Parent = root;
        var ticker = Play(root);

        destroyed.Destroy();
        ticker.StepFrame();

        Assert.Equal([kept], root.Children);
        Assert.True(destroyed.IsDestroyed);
    }
}
