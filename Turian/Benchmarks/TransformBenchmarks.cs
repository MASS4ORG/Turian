/// <summary>Measures transform propagation through the Node hierarchy: edit local transforms, read every world one.</summary>
static class TransformBenchmarks
{
    const int nodeCount = 100_000;
    const int runs = 11;
    const int sparseStride = 10;

    public static void Run()
    {
        Console.WriteLine($"Transforms — {nodeCount:N0} nodes, 4-ary tree, median of {runs} runs");
        Console.WriteLine("Frame = edit local transforms, then read every world transform");
        Console.WriteLine();

        var scene = NodeScene.Build();
        Console.WriteLine($"{"",-24} {"all dirty",12} {"10% dirty",12} {"alloc/frame",14}");
        var full = Measure(scene.Frame, 1);
        var part = Measure(scene.Frame, sparseStride);
        var allocated = Allocated(scene.Frame);
        Console.WriteLine($"{"Node",-24} {full,9:F2} ms {part,9:F2} ms {allocated / 1024.0,11:F0} KiB");
        Console.WriteLine($"{"Retained per node",-24} {NodeScene.BytesPerNode(),9:F0} B");
    }

    static double Measure(Action<int> frame, int stride)
    {
        for (var i = 0; i < 3; i++) frame(stride);
        var times = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            GC.Collect();
            var watch = Stopwatch.StartNew();
            frame(stride);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[runs / 2];
    }

    static long Allocated(Action<int> frame)
    {
        frame(1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        frame(1);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static int Parent(int i) => i == 0 ? -1 : (i - 1) / 4;

    sealed class NodeScene
    {
        readonly Node[] nodes = new Node[nodeCount];
        float tick;

        public static NodeScene Build()
        {
            var scene = new NodeScene();
            for (var i = 0; i < nodeCount; i++)
            {
                var node = new Node();
                var parent = Parent(i);
                if (parent >= 0)
                {
                    scene.nodes[parent].Children.Add(node);
                    node.Parent = scene.nodes[parent];
                }

                scene.nodes[i] = node;
            }

            return scene;
        }

        public static double BytesPerNode()
        {
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            var scene = Build();
            GC.Collect();
            var after = GC.GetTotalMemory(true);
            GC.KeepAlive(scene);
            return (after - before) / (double)nodeCount;
        }

        public void Frame(int stride)
        {
            tick += 0.01f;
            for (var i = 0; i < nodeCount; i += stride)
                nodes[i].Position = nodes[i].Position with { X = tick };
            var sum = 0f;
            for (var i = 0; i < nodeCount; i++)
                sum += nodes[i].GlobalTransform.Position.X;
            GC.KeepAlive(sum);
        }
    }
}
