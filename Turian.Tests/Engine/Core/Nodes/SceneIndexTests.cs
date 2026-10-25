namespace Turian.Tests;

/// <summary>
/// The scene index behind frame callbacks, primary cameras and statistics displays must be invisible to game code:
/// callbacks run in depth-first scene order and registration order within a node, as a full hierarchy walk would.
/// </summary>
[Collection(SerialTests.Name)]
public class SceneIndexTests
{
    sealed class Plain : Component;

    sealed class Recorder : Component
    {
        public required List<string> Log { get; init; }
        public required string Label { get; init; }

        public override void OnFixedUpdate(float fixedDeltaTime) => Log.Add($"{Label}:fixed");
        public override void OnUpdate(float deltaTime) => Log.Add($"{Label}:update");
        public override void OnLateUpdate(float deltaTime) => Log.Add($"{Label}:late");
    }

    sealed class RecordingNode : Node
    {
        public required List<string> Log { get; init; }

        public override void OnUpdate(float deltaTime) => Log.Add($"{Name}:update");
    }

    sealed class Spawner : Component
    {
        public required Action Spawn { get; init; }

        public override void OnUpdate(float deltaTime) => Spawn();
    }

    sealed class Display : Component, IRenderStatisticsTarget
    {
        public bool Recording { get; set; } = true;
        public bool IsRecording => Recording;
        public FrameStatistics Statistics { get; } = new();
        public double? SceneLoadMilliseconds { get; set; }
    }

    static SceneTicker Adopt(params Node[] roots)
    {
        var manager = new SceneManager(new AssetDatabase());
        foreach (var root in roots)
        {
            root.Awake(null);
            manager.AdoptScene(Guid.NewGuid(), root, LoadSceneMode.Additive);
        }

        return new SceneTicker(manager);
    }

    static Recorder Record(Node node, List<string> log, string label)
    {
        var recorder = new Recorder { Log = log, Label = label };
        node.Components.Add(recorder);
        return recorder;
    }

    /// <summary>
    /// Nodes without callbacks are skipped transparently: each pass runs depth-first, a node's own callback before its
    /// components, and components in registration order. Reordering siblings or components reorders the next frame.
    /// </summary>
    [Fact]
    public void CallbacksFollowSceneAndRegistrationOrder()
    {
        var log = new List<string>();
        var root = new Node { Name = "root" };
        Record(root, log, "r1");
        root.Components.Add(new Plain());
        Record(root, log, "r2");
        var plain = new Node { Name = "plain" };
        plain.Components.Add(new Plain());
        var nested = new Node { Name = "nested" };
        Record(nested, log, "a1");
        plain.Children.Add(nested);
        var custom = new RecordingNode { Name = "custom", Log = log };
        Record(custom, log, "b1");
        var last = new Node { Name = "last" };
        Record(last, log, "c1");
        root.Children = [plain, custom, last];
        var ticker = Adopt(root);

        ticker.StepFrame();
        string[] order = ["r1", "r2", "a1", "custom", "b1", "c1"];
        Assert.Equal(Expected(order, "fixed").Concat(Expected(order, "update")).Concat(Expected(order, "late")), log);
        Assert.DoesNotContain(plain, root.EnsureSceneIndex().Nodes(SceneIndex.Kinds.Callbacks));

        log.Clear();
        root.Children.Move(2, 0);
        root.Components.Move(0, 2);
        ticker.StepFrame();
        order = ["r2", "r1", "c1", "a1", "custom", "b1"];
        Assert.Equal(Expected(order, "fixed").Concat(Expected(order, "update")).Concat(Expected(order, "late")), log);
    }

    static IEnumerable<string> Expected(string[] labels, string pass) =>
        labels.Where(label => pass == "update" || label != "custom").Select(label => $"{label}:{pass}");

    /// <summary>
    /// Membership is captured at the start of each pass, as the hierarchy walk did: a node, or a callback component on a
    /// node without callbacks, added during Update joins from the late pass, and a removed node keeps the running pass
    /// but no later one.
    /// </summary>
    [Fact]
    public void MidFrameChangesJoinAtTheNextPass()
    {
        var log = new List<string>();
        var root = new Node { Name = "root" };
        var plain = new Node { Name = "plain" };
        plain.Components.Add(new Plain());
        var leaving = new Node { Name = "leaving" };
        Record(leaving, log, "leaving");
        var spawned = new Node { Name = "spawned" };
        Record(spawned, log, "spawned");
        var spawns = 0;
        root.Components.Add(new Spawner
        {
            Spawn = () =>
            {
                if (spawns++ != 1) { return; }
                root.Children.Add(spawned);
                Record(plain, log, "added");
                root.Children.Remove(leaving);
            },
        });
        root.Children = [plain, leaving];
        var ticker = Adopt(root);
        ticker.StepFrame();
        log.Clear();

        ticker.StepFrame();
        Assert.Equal(["leaving:fixed", "leaving:update", "added:late", "spawned:late"], log);

        log.Clear();
        ticker.StepFrame();
        Assert.Equal(["added:fixed", "spawned:fixed", "added:update", "spawned:update", "added:late", "spawned:late"],
            log);
    }

    /// <summary>A node moved to another scene and back between frames is listed once in each hierarchy it joins.</summary>
    [Fact]
    public void MovesBetweenHierarchiesListOnce()
    {
        var log = new List<string>();
        Node first = new() { Name = "first" }, second = new() { Name = "second" };
        var traveller = new Node { Name = "traveller" };
        Record(traveller, log, "t");
        first.Children.Add(traveller);
        var ticker = Adopt(first, second);
        ticker.StepFrame();

        second.Children.Add(traveller);
        first.Children.Add(traveller);
        second.Children.Add(traveller);
        log.Clear();
        ticker.StepFrame();

        Assert.Equal(["t:fixed", "t:update", "t:late"], log);
        Assert.Empty(first.EnsureSceneIndex().Nodes(SceneIndex.Kinds.Callbacks));
        Assert.Equal([traveller], second.EnsureSceneIndex().Nodes(SceneIndex.Kinds.Callbacks));

        traveller.Parent = null;
        traveller.DestroyImmediate();
        Assert.Empty(second.EnsureSceneIndex().Nodes(SceneIndex.Kinds.Callbacks));
    }

    /// <summary>The statistics display is the first recording, active one in hierarchy order, kept current by edits.</summary>
    [Fact]
    public void StatisticsTargetFollowsHierarchyAndRecording()
    {
        var root = new Node();
        Node a = new(), b = new();
        root.Children = [a, b];
        var first = new Display();
        var second = new Display();
        a.Components.Add(first);
        b.Components.Add(second);

        Assert.Same(first, RenderStatisticsTargets.Find(root));
        first.Recording = false;
        Assert.Same(second, RenderStatisticsTargets.Find(root));
        first.Recording = true;
        a.IsActive = false;
        Assert.Same(second, RenderStatisticsTargets.Find(root));
        a.IsActive = true;
        root.Children.Move(1, 0);
        Assert.Same(second, RenderStatisticsTargets.Find(root));
        b.Components.Remove(second);
        Assert.Same(first, RenderStatisticsTargets.Find(root));
        first.IsActive = false;
        Assert.Null(RenderStatisticsTargets.Find(root));

        first.IsActive = true;
        Assert.Same(first, RenderStatisticsTargets.Find(a));
        Node hidden = new() { IsActive = false }, nested = new();
        var third = new Display();
        nested.Components.Add(third);
        a.Children = [hidden, nested];
        first.Recording = false;
        Assert.Same(third, RenderStatisticsTargets.Find(a));
        nested.IsActive = false;
        Assert.Null(RenderStatisticsTargets.Find(a));
        root.IsActive = false;
        Assert.Null(RenderStatisticsTargets.Find(root));
    }

    /// <summary>A query rooted below the hierarchy root searches only that subtree, ignoring inactive ancestors above it.</summary>
    [Fact]
    public void SubtreeQueriesSearchTheSubtree()
    {
        var root = new Node();
        Node branch = new(), outside = new();
        root.Children = [outside, branch];
        var inner = branch.AddComponent(new CameraComponent());
        outside.AddComponent(new CameraComponent { Priority = 5 });

        Assert.Same(inner, CameraComponent.FindPrimary(branch));
        root.IsActive = false;
        Assert.Same(inner, CameraComponent.FindPrimary(branch));
        Assert.Null(CameraComponent.FindPrimary(root));
    }
}
