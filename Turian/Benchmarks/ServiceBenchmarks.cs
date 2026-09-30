static class ServiceBenchmarks
{
    const int componentCount = 10_000;
    const int runs = 11;

    sealed class PlainComponent : Component;

    sealed class InjectedComponent : Component
    {
        [InjectService, JsonIgnore]
        public IInputSource? Input { get; private set; }
    }

    public static void Run()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IInputSource>(new BufferedInputSource())
            .BuildServiceProvider();

        // Populate the per-type injection cache before measuring typical loaded-scene behavior.
        var warm = BuildScene(static () => new InjectedComponent());
        warm.Awake(null, provider);

        BuildScene(static () => new PlainComponent()).Awake(null);
        BuildScene(static () => new PlainComponent()).Awake(null, provider);
        var cases = new (string Name, Func<Node> Create, IServiceProvider? Provider)[]
        {
            ("No provider, no injected properties", () => BuildScene(static () => new PlainComponent()), null),
            ("Provider, no injected properties", () => BuildScene(static () => new PlainComponent()), provider),
            ("Provider, one injected property", () => BuildScene(static () => new InjectedComponent()), provider),
        };
        var samples = Enumerable.Range(0, cases.Length).Select(_ => new List<double>(runs)).ToArray();
        for (var run = 0; run < runs; run++)
            for (var offset = 0; offset < cases.Length; offset++)
            {
                var index = (run + offset) % cases.Length;
                samples[index].Add(Measure(cases[index].Create, cases[index].Provider));
            }

        Console.WriteLine($"Scene Awake, {componentCount:N0} components, median of {runs} runs");
        for (var i = 0; i < cases.Length; i++)
        {
            samples[i].Sort();
            Console.WriteLine($"{cases[i].Name,-36} {samples[i][runs / 2],8:F2} ms");
        }
    }

    static Node BuildScene(Func<Component> componentFactory)
    {
        var root = new Node();
        for (var i = 0; i < componentCount; i++)
        {
            var child = new Node();
            child.Components.Add(componentFactory());
            root.Children.Add(child);
        }

        return root;
    }

    static double Measure(Func<Node> create, IServiceProvider? provider)
    {
        var scene = create();
        GC.Collect();
        var watch = Stopwatch.StartNew();
        if (provider is null) scene.Awake(null);
        else scene.Awake(null, provider);
        watch.Stop();
        GC.KeepAlive(scene);
        return watch.Elapsed.TotalMilliseconds;
    }
}
