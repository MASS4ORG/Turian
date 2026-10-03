# Tools

Single-file C# helpers (.NET 10 file-based apps). Run from the repository root:

| Tool | What it does |
|---|---|
| `dotnet run tools/crap.cs -- [--base main] [--max 15] [--all]` | CRAP scores of the methods changed since `--base` (working tree and untracked files included), from `coverage/coverage.xml`. Exits 1 when one is over `--max`. Run `./build.sh test` first. |
| `dotnet run tools/imgdiff.cs -- <expected.png> <actual.png> [--tolerance 2] [--out mask.png]` | Pixel comparison of two images: differing count, largest delta, bounding box, optional mask. Exits 1 when they differ. |
| `dotnet run tools/render-bench.cs -- <project> [--runs 3] [--frames 300]` | Runs headless play mode in Release several times and summarizes the median render time and per-frame allocations. |
| `dotnet run tools/slow-tests.cs -- [coverage/trx] [--top 15]` | Slowest tests and classes from a TRX report, with each test's start offset. Make one with `dotnet test --project Turian.Tests/Turian.Tests.csproj --report-xunit-trx --results-directory coverage/trx`. |

`imgdiff.cs` pulls SkiaSharp (the version Guinevere already uses) through a `#:package` directive.
