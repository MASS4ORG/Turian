// Run: dotnet run -c Release --project Turian.Benchmarks
using System.Diagnostics;
using Turian;
using Turian.Engine.Core;

const int assetCount = 10_000;
const int linksPerAsset = 10;
const int sceneNodes = 10_000;
const int runs = 11;

var root = Path.Combine(Path.GetTempPath(), $"turian-bench-{Guid.NewGuid():N}");
var assets = Path.Combine(root, "Assets");
Directory.CreateDirectory(assets);
var database = new AssetDatabase();

try
{
    Console.WriteLine($"Turian benchmarks — median of {runs} runs, {Environment.ProcessorCount} cores");
    Console.WriteLine();

    var plain = CreateAssets("plain", _ => new Stats { Health = 100, Speed = 4.5f, Name = "Goblin" });
    var linked = new Guid[assetCount];
    var linkedIds = CreateAssets("linked", i => new Stats
    {
        Links = [.. Enumerable.Range(1, linksPerAsset).Select(k => new Stats { Id = linked[(i + k) % assetCount] })],
    }, linked);
    WarmFileCache();

    Report("Load 10,000 DataAssets (parallel preload, generated)", () => Preload(plain));
    Report("Load 10,000 DataAssets (one at a time, generated)", () => LoadSequentially(plain));
    WithoutGenerated(typeof(Stats), () =>
        Report("Load 10,000 DataAssets (parallel preload, reflection)", () => Preload(plain)));
    Report($"Load 10,000 DataAssets resolving {assetCount * linksPerAsset:N0} references", () => Preload(linkedIds));

    var loader = new RuntimeAssetLoader(database);
    var shared = loader.LoadContentAsync<Stats>(plain[0]).GetAwaiter().GetResult()!;
    var local = new Stats { Health = 100 };
    Console.WriteLine();
    Console.WriteLine($"Read a hot field, shared DataAsset:  {ReadNanoseconds(shared):F2} ns/read");
    Console.WriteLine($"Read a hot field, plain object:      {ReadNanoseconds(local):F2} ns/read");

    Console.WriteLine();
    var direct = Serializer.Serialize(BuildScene(static target => new DirectLink { Target = target }));
    var byId = Serializer.Serialize(BuildScene(static target => new IdLink { Target = new NodeRef<Node>(target.Id) }));
    var directLoad = Report($"Load a {sceneNodes:N0}-node scene, direct Node fields", () => LoadScene(direct));
    var idLoad = Report($"Load a {sceneNodes:N0}-node scene, NodeRef<Node> fields", () => LoadScene(byId));
    var idResolve = Report($"Load a {sceneNodes:N0}-node scene, NodeRef<Node> fields, resolve all", () =>
    {
        var scene = LoadScene(byId);
        foreach (var node in scene.Children)
            _ = node.GetComponent<IdLink>()!.Target!.Resolve(scene);
    });
    Console.WriteLine($"Direct references cost {(directLoad / idLoad - 1) * 100:F1}% over NodeRef load-only, "
                      + $"{(directLoad / idResolve - 1) * 100:F1}% against NodeRef load-and-resolve");
}
finally
{
    Directory.Delete(root, recursive: true);
}

return;

Guid[] CreateAssets(string prefix, Func<int, Stats> create, Guid[]? ids = null)
{
    ids ??= new Guid[assetCount];
    var metas = new DataAssetAsset[assetCount];
    for (var i = 0; i < assetCount; i++)
    {
        var path = Path.Combine(assets, $"{prefix}-{i}.asset");
        metas[i] = new DataAssetAsset { RelativePath = path };
        ids[i] = metas[i].Id;
    }

    for (var i = 0; i < assetCount; i++)
    {
        var path = metas[i].RelativePath;
        Serializer.Save<DataAsset>(path, create(i));
        File.WriteAllText($"{path}.meta", Serializer.Serialize(metas[i]));
        database.RegisterAsset(metas[i]);
    }

    return ids;
}

void WarmFileCache()
{
    foreach (var file in Directory.EnumerateFiles(assets)) _ = File.ReadAllBytes(file);
}

void Preload(Guid[] ids) => new RuntimeAssetLoader(database).PreloadAsync(ids).GetAwaiter().GetResult();

void LoadSequentially(Guid[] ids)
{
    var loader = new RuntimeAssetLoader(database);
    foreach (var id in ids) _ = loader.LoadContentAsync<Stats>(id).GetAwaiter().GetResult();
}

static void WithoutGenerated(Type type, Action run)
{
    GeneratedSerializers.Remove(type, out var serializer);
    try
    {
        run();
    }
    finally
    {
        GeneratedSerializers.Register(type, serializer!);
    }
}

static double ReadNanoseconds(Stats stats)
{
    const int reads = 200_000_000;
    var sum = 0L;
    var watch = Stopwatch.StartNew();
    for (var i = 0; i < reads; i++) sum += stats.Health;
    watch.Stop();
    GC.KeepAlive(sum);
    return watch.Elapsed.TotalNanoseconds / reads;
}

static Node BuildScene(Func<Node, Component> link)
{
    var scene = new Node { Name = "Scene" };
    var nodes = Enumerable.Range(0, sceneNodes).Select(i => new Node { Name = $"Node {i}" }).ToList();
    for (var i = 0; i < sceneNodes; i++)
    {
        nodes[i].AddComponent(link(nodes[(i + 1) % sceneNodes]));
        scene.Children.Add(nodes[i]);
    }

    return scene;
}

static Node LoadScene(string json) => Serializer.LoadData<Node>(json)!;

static double Report(string label, Action run)
{
    run();
    var times = new List<double>();
    for (var i = 0; i < runs; i++)
    {
        GC.Collect();
        var watch = Stopwatch.StartNew();
        run();
        times.Add(watch.Elapsed.TotalMilliseconds);
    }

    times.Sort();
    var median = times[runs / 2];
    Console.WriteLine($"{label,-72} {median,9:F1} ms");
    return median;
}

/// <summary>A small DataAsset, optionally linking to others.</summary>
[TypeId("b0000001-0000-4000-8000-000000000001")]
public sealed class Stats : DataAsset
{
    /// <summary>A hot value.</summary>
    public int Health { get; set; }

    /// <summary>Another value.</summary>
    public float Speed { get; set; }

    /// <summary>A label.</summary>
    public string? Name { get; set; }

    /// <summary>References to other DataAssets.</summary>
    public List<Stats>? Links { get; set; }
}

/// <summary>A component referencing a node directly.</summary>
[TypeId("b0000001-0000-4000-8000-000000000002")]
public sealed class DirectLink : Component
{
    /// <summary>The referenced node.</summary>
    public Node? Target { get; set; }
}

/// <summary>A component referencing a node by id.</summary>
[TypeId("b0000001-0000-4000-8000-000000000003")]
public sealed class IdLink : Component
{
    /// <summary>The referenced node.</summary>
    public NodeRef<Node>? Target { get; set; }
}
