#!/usr/bin/env dotnet
// Runs headless play mode several times in Release and summarizes the render line each run prints: median render
// time and the last frame's managed allocations. Use enough frames to get past texture streaming (300 for Bistro).
//
// Usage: dotnet run tools/render-bench.cs -- <project> [--runs <n>] [--frames <n>] [--out <frame.png>]
//   --runs    play sessions to run (default: 3)
//   --frames  frames per session (default: 300)
//   --out     where each session writes its last frame (default: a temporary file)

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: dotnet run tools/render-bench.cs -- <project> [--runs n] [--frames n] [--out png]");
    return 2;
}

var project = args[0];
var runs = int.Parse(Option("--runs") ?? "3", CultureInfo.InvariantCulture);
var frames = Option("--frames") ?? "300";
var output = Option("--out") ?? Path.Combine(Path.GetTempPath(), "turian-render-bench.png");
var cli = Path.Combine(Directory.GetCurrentDirectory(), "Turian", "Editor", "CLI");
var summary = new Regex(@"Render: median ([\d.]+) ms over (\d+) frames, last frame allocated ([\d.]+) KiB");

var medians = new List<double>();
for (var run = 1; run <= runs; run++)
{
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[]
             {
                 "run", "-c", "Release", "--project", cli, "--", "playmode", project, "--frames", frames, "--out", output,
             })
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (summary.Match(stdout) is not { Success: true } match)
    {
        Console.Error.WriteLine($"Run {run}: no render summary (exit {process.ExitCode}).");
        Console.Error.WriteLine(stdout + await stderr);
        return 1;
    }

    var median = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    medians.Add(median);
    Console.WriteLine($"Run {run}: median {median:F2} ms over {match.Groups[2].Value} frames, " +
        $"last frame allocated {match.Groups[3].Value} KiB");
}

medians.Sort();
Console.WriteLine($"Median of {runs} run(s): {medians[medians.Count / 2]:F2} ms " +
    $"(range {medians[0]:F2}–{medians[^1]:F2} ms)");
return 0;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
