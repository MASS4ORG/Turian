using System.Numerics;
using System.Runtime.InteropServices;
using Turian.Benchmarks.Legacy;

/// <summary>Measures geometry decoding without file I/O or GPU upload.</summary>
static class MeshFormatBenchmarks
{
    const int samples = 9;

    /// <summary>Reports equivalent AMMESH, interleaved GLB, and planar GLB geometry loads.</summary>
    public static void Run()
    {
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; " +
                          $"{Environment.ProcessorCount} CPUs; warmed median of {samples} samples");
        Console.WriteLine("Vertices,Format,Bytes,Microseconds,AllocatedBytes");
        foreach (var count in new[] { 300, 30_000, 300_000 })
        {
            var vertices = Enumerable.Range(0, count).Select(i => new Vertex(
                new Vector3(i % 100, i / 100, i % 7), Vector3.One)
            {
                Normal = Vector3.UnitY,
                Uv = new Vector2(i % 2, 0),
                Tangent = new Vector4(1, 0, 0, 1),
            }).ToArray();
            var indices = Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
            var ammesh = MeshFormatFixtures.Ammesh(vertices, indices);
            var interleaved = MeshFormatFixtures.Glb(vertices, indices, planar: false);
            var planar = MeshFormatFixtures.Glb(vertices, indices, planar: true);
            var planarWithoutColor = MeshFormatFixtures.Glb(vertices, indices, planar: true, omitColor: true);
            var cases = new (string Name, byte[] Bytes, Func<ModelBuilder> Load)[]
            {
                ("AMMESH baseline", ammesh, () => LegacyAmmesh.Read(ammesh).ToModelBuilder()),
                ("GLB interleaved", interleaved, () => GltfModelReader.Read(interleaved)),
                ("GLB planar", planar, () => GltfModelReader.Read(planar)),
                ("GLB planar without color", planarWithoutColor, () => GltfModelReader.Read(planarWithoutColor)),
            };
            foreach (var item in cases)
            {
                var loaded = item.Load();
                if (!loaded.Vertices.SequenceEqual(vertices) || !loaded.Indices.SequenceEqual(indices))
                    throw new InvalidOperationException($"{item.Name} did not preserve benchmark geometry.");
                if (loaded.SubMeshes.Count != 1 || loaded.SubMeshes[0].IndexCount != count)
                    throw new InvalidOperationException($"{item.Name} did not preserve the benchmark submesh.");
                for (var i = 0; i < 20; i++) GC.KeepAlive(item.Load());
            }

            var iterations = count < 1000 ? 1000 : count < 100_000 ? 50 : 10;
            var timings = cases.Select(_ => new List<(double Time, long Alloc)>()).ToArray();
            for (var sample = 0; sample < samples; sample++)
                for (var offset = 0; offset < cases.Length; offset++)
                {
                    var index = (sample + offset) % cases.Length;
                    timings[index].Add(Measure(cases[index].Load, iterations));
                }
            for (var i = 0; i < cases.Length; i++)
            {
                var result = timings[i].OrderBy(value => value.Time).ElementAt(samples / 2);
                Console.WriteLine($"{count},{cases[i].Name},{cases[i].Bytes.Length},{result.Time:F2},{result.Alloc}");
            }
        }
    }

    static (double Time, long Alloc) Measure(Func<ModelBuilder> load, int iterations)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) GC.KeepAlive(load());
        watch.Stop();
        return (watch.Elapsed.TotalMicroseconds / iterations,
            (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations);
    }
}
