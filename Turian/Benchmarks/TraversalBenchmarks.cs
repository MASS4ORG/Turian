/// <summary>Measures scene-wide component queries over a large hierarchy, as render systems run them each frame.</summary>
static class TraversalBenchmarks
{
    const int nodeCount = 100_000;
    const int componentStride = 10;
    const int runs = 11;

    sealed class Marker : Component;

    public static void Run()
    {
        var root = BuildScene();
        var results = new List<Marker>();
        Console.WriteLine($"GetComponentsInChildren — {nodeCount:N0} nodes, 4-ary tree, a component on every " +
            $"{componentStride}th node, median of {runs} runs");
        Report("Enumerable (foreach)", () =>
        {
            var count = 0;
            foreach (var _ in root.GetComponentsInChildren<Marker>()) count++;
            return count;
        });
        Report("Into a reused List<T>", () =>
        {
            root.GetComponentsInChildren(results);
            return results.Count;
        });
    }

    static Node BuildScene()
    {
        var nodes = new Node[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            nodes[i] = new Node();
            if (i % componentStride == 0) nodes[i].Components.Add(new Marker());
            if (i == 0) continue;
            var parent = nodes[(i - 1) / 4];
            nodes[i].Parent = parent;
            parent.Children.Add(nodes[i]);
        }

        return nodes[0];
    }

    static void Report(string label, Func<int> query)
    {
        for (var i = 0; i < 3; i++) query();
        var times = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            GC.Collect();
            var watch = Stopwatch.StartNew();
            query();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = query();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        times.Sort();
        Console.WriteLine($"{label,-28} {times[runs / 2],8:F2} ms {allocated / 1024.0,10:F0} KiB  ({found:N0} found)");
    }
}
