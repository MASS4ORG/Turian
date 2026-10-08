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

Compares identical AMMESH and GLB geometry across nine warmed samples, reporting load time, file size and allocations. Excludes I/O, materials and GPU upload; AMMESH exists only as a benchmark baseline.
