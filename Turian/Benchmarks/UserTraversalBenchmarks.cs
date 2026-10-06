using System.Numerics;

/// <summary>
/// Typical gameplay-script queries over a busy scene, written two ways: the public query API with BCL LINQ, and
/// hand-written loops over the allocation-free list query.
/// </summary>
static class UserTraversalBenchmarks
{
    const int nodeCount = 50_000;
    const int ancestorQueries = 1_000;
    const int runs = 11;
    const float radius = 40f;

    sealed class Enemy : Component
    {
        public int Health;
    }

    sealed class Pickup : Component;

    sealed class Squad : Component;

    sealed record Scene(Node Root, Node[] Leaves, Vector3 Player);

    delegate double Query(Scene scene);

    public static void Run()
    {
        var scene = BuildScene();
        var queries = new (string Name, Query Bcl, Query Loops)[]
        {
            ("Count enemies near the player", BclNearby, LoopNearby),
            ("Sum enemy health", BclHealth, LoopHealth),
            ("Find a node by name", BclFind, LoopFind),
            ("Nearest pickup", BclNearest, LoopNearest),
            ("Squad of 1,000 leaves (ancestors)", BclSquads, LoopSquads),
            ("10 weakest enemies", BclWeakest, LoopWeakest),
        };

        Console.WriteLine($"User-code queries — {nodeCount:N0} nodes, 4-ary tree, median of {runs} runs (ms / KiB per call)");
        Console.WriteLine($"{"",-36} {"BCL LINQ",20} {"Hand loops",20}");
        foreach (var (name, bcl, loops) in queries)
        {
            var expected = bcl(scene);
            if (loops(scene) != expected)
                throw new InvalidOperationException($"{name}: variants disagree");
            Console.WriteLine($"{name,-36} {Cell(bcl, scene),20} {Cell(loops, scene),20}");
        }
    }

    static Scene BuildScene()
    {
        var random = new Random(42);
        var nodes = new Node[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            var node = new Node
            {
                Name = i == nodeCount - 1 ? "Boss" : $"Node {i}",
                Position = new Vector3(random.NextSingle() * 20 - 10, 0, random.NextSingle() * 20 - 10),
            };
            if (i % 20 == 0) node.AddComponent(new Enemy { Health = random.Next(1, 100) });
            if (i % 50 == 7) node.AddComponent(new Pickup());
            if (i is >= 21 and <= 84) node.AddComponent(new Squad());
            if (i > 0)
            {
                var parent = nodes[(i - 1) / 4];
                node.Parent = parent;
                parent.Children.Add(node);
            }

            nodes[i] = node;
        }

        return new Scene(nodes[0], nodes[^ancestorQueries..], new Vector3(25, 0, 25));
    }

    static string Cell(Query query, Scene scene)
    {
        for (var i = 0; i < 3; i++) query(scene);
        var times = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            query(scene);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        query(scene);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        times.Sort();
        return $"{times[runs / 2],7:F3} / {allocated / 1024.0,7:F1}";
    }

    static bool Near(Component component, Vector3 player) =>
        Vector3.DistanceSquared(component.Node!.GlobalTransform.Position, player) < radius * radius;

    static float Distance(Component component, Vector3 player) =>
        Vector3.DistanceSquared(component.Node!.GlobalTransform.Position, player);

    // Today: the component query plus System.Linq, and a Parent loop for ancestors.

    static double BclNearby(Scene s) => s.Root.GetComponentsInChildren<Enemy>().Count(e => Near(e, s.Player));

    static double BclHealth(Scene s) => s.Root.GetComponentsInChildren<Enemy>().Sum(e => e.Health);

    static double BclFind(Scene s) => Node.GetChildren(s.Root).FirstOrDefault(n => n.Name == "Boss") is null ? 0 : 1;

    static double BclNearest(Scene s) =>
        Distance(s.Root.GetComponentsInChildren<Pickup>().MinBy(p => Distance(p, s.Player))!, s.Player);

    static double BclSquads(Scene s)
    {
        var found = 0;
        foreach (var leaf in s.Leaves)
        {
            for (var node = leaf.Parent; node is not null; node = node.Parent)
            {
                if (!node.HasComponent<Squad>()) continue;
                found++;
                break;
            }
        }

        return found;
    }

    static double BclWeakest(Scene s) =>
        s.Root.GetComponentsInChildren<Enemy>().OrderBy(e => e.Health).Take(10).Sum(e => e.Health);

    // Hand loops: the allocation-free list query with reused buffers.

    static readonly List<Enemy> Enemies = [];
    static readonly List<Pickup> Pickups = [];
    static readonly Stack<Node> Pending = new();

    static double LoopNearby(Scene s)
    {
        s.Root.GetComponentsInChildren(Enemies);
        var count = 0;
        foreach (var enemy in Enemies)
            if (Near(enemy, s.Player))
                count++;
        return count;
    }

    static double LoopHealth(Scene s)
    {
        s.Root.GetComponentsInChildren(Enemies);
        var sum = 0;
        foreach (var enemy in Enemies) sum += enemy.Health;
        return sum;
    }

    static double LoopFind(Scene s)
    {
        Pending.Clear();
        for (var i = s.Root.Children.Count - 1; i >= 0; i--) Pending.Push(s.Root.Children[i]);
        while (Pending.TryPop(out var node))
        {
            if (node.Name == "Boss") return 1;
            for (var i = node.Children.Count - 1; i >= 0; i--) Pending.Push(node.Children[i]);
        }

        return 0;
    }

    static double LoopNearest(Scene s)
    {
        s.Root.GetComponentsInChildren(Pickups);
        var best = float.MaxValue;
        foreach (var pickup in Pickups) best = MathF.Min(best, Distance(pickup, s.Player));
        return best;
    }

    static double LoopSquads(Scene s) => BclSquads(s);

    static double LoopWeakest(Scene s)
    {
        s.Root.GetComponentsInChildren(Enemies);
        Enemies.Sort(static (a, b) => a.Health.CompareTo(b.Health));
        var sum = 0;
        for (var i = 0; i < Math.Min(10, Enemies.Count); i++) sum += Enemies[i].Health;
        return sum;
    }
}
