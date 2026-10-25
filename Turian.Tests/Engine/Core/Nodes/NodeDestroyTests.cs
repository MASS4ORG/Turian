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
    /// and advances the collection version for each removal.
    /// </summary>
    [Fact]
    public void Destroy_FromUpdateFlushesAtFrameEndWithVersionChanges()
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
        var version = group.Children.Version;
        var notifications = 0;
        var observedComplete = false;
        group.Children.StructureChanged += (_, _) =>
        {
            notifications++;
            observedComplete = group.Children.SequenceEqual([keep]) &&
                nodes.All(static node => node.IsDestroyed && node.Parent is null);
        };

        ticker.StepFrame();

        Assert.Equal([keep], group.Children);
        Assert.Equal(version + nodes.Length, group.Children.Version);
        Assert.Equal(1, notifications);
        Assert.True(observedComplete);
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

    /// <summary>Assigned children are copied into the owned collection and participate in deferred destruction.</summary>
    [Fact]
    public void Destroy_RemovesChildrenCopiedFromABuilder()
    {
        Node kept = new(), destroyed = new();
        var root = new Node();
        NodeCollection builder = [destroyed, kept];
        var owned = root.Children;
        root.Children = builder;
        builder.Clear();
        Assert.Same(owned, root.Children);
        var ticker = Play(root);

        destroyed.Destroy();
        ticker.StepFrame();

        Assert.Equal([kept], root.Children);
        Assert.True(destroyed.IsDestroyed);
    }

    /// <summary>A failing parent observer cannot suppress another parent's notification or future flushes.</summary>
    [Fact]
    public void Destroy_ObserverFailureDoesNotBlockOtherParents()
    {
        var left = new Node { Children = [new Node(), new Node()] };
        var right = new Node { Children = [new Node()] };
        var root = new Node { Children = [left, right] };
        var ticker = Play(root);
        EventHandler throwing = (_, _) => throw new InvalidOperationException("observer failed");
        left.Children.StructureChanged += throwing;
        var rightNotifications = 0;
        right.Children.StructureChanged += (_, _) => rightNotifications++;
        left.Children[0].Destroy();
        right.Children[0].Destroy();

        ticker.StepFrame();

        Assert.Single(left.Children);
        Assert.Empty(right.Children);
        Assert.Equal(1, rightNotifications);
        left.Children.StructureChanged -= throwing;
        var leftNotifications = 0;
        left.Children.StructureChanged += (_, _) => leftNotifications++;
        left.Children[0].Destroy();
        ticker.StepFrame();
        Assert.Empty(left.Children);
        Assert.Equal(1, leftNotifications);
    }

    /// <summary>A destruction requested by a structural observer remains queued for the next flush boundary.</summary>
    [Fact]
    public void Destroy_NotificationKeepsNewRequestsForTheNextBoundary()
    {
        Node first = new(), second = new();
        var group = new Node { Children = [first, second] };
        var ticker = Play(new Node { Children = [group] });
        var notifications = 0;
        group.Children.StructureChanged += (_, _) =>
        {
            notifications++;
            second.Destroy();
        };
        first.Destroy();

        ticker.Tick(0);

        Assert.True(first.IsDestroyed);
        Assert.False(second.IsDestroyed);
        Assert.True(second.DestroyQueued);
        Assert.Equal([second], group.Children);
        ticker.Tick(0);
        Assert.True(second.IsDestroyed);
        Assert.Empty(group.Children);
        Assert.Equal(2, notifications);
    }
}
