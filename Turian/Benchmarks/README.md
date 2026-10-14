# Turian benchmarks

Performance measurements for engine serialization, DataAsset workflows, and mesh loading.

From the repository root:

```sh
dotnet run -c Release --project Turian/Benchmarks
```

Run in Release configuration with .NET 10 to compare results.

## Mesh formats

```sh
dotnet run -c Release --property:TieredCompilation=false --project Turian/Benchmarks -- --mesh-formats
```

Compares identical interleaved and planar GLB geometry across nine warmed samples, reporting load time, file size and allocations. Excludes I/O, materials and GPU upload.

## Gameplay async

```sh
dotnet run -c Release --project Turian/Benchmarks -- --async
```

Measures 10,000 repeating NextFrame waits and 10,000 pending duration waits independently of scene traversal. After 256 warm-up frames, seven samples of 1,000 frames report median nanoseconds per wait per frame and require exactly zero allocated bytes. Startup, cancellation and debug creation stacks are excluded. Initial budgets are 500 ns for resumed waits and 200 ns for pending waits on the reference machine; compare runs on the same machine and runtime.
