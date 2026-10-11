#!/usr/bin/env dotnet
// Lists the slowest tests and test classes from a TRX report, to find what makes the suite slow.
//
// Usage: dotnet run tools/slow-tests.cs -- [<report.trx | directory>] [--top <n>]
//   report     a .trx file, or a directory whose newest .trx is used (default: coverage/)
//   --top      how many tests and classes to list (default: 15)
//
// The NUKE `test` target writes coverage/<host>.trx (the newest is picked up automatically), so run `./build.sh test`
// first. To produce one by hand:
//   dotnet run --project Turian.Tests/Turian.Tests.csproj -- --report-xunit-trx --results-directory coverage

using System.Globalization;
using System.Xml.Linq;

var top = int.Parse(Option("--top") ?? "15", CultureInfo.InvariantCulture);
var target = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)
    && Array.IndexOf(args, arg) is var i && (i == 0 || args[i - 1] != "--top")) ?? "coverage";
var report = Directory.Exists(target)
    ? new DirectoryInfo(target).GetFiles("*.trx", SearchOption.AllDirectories)
        .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName
    : target;
if (report is null || !File.Exists(report))
{
    Console.Error.WriteLine($"No TRX report at {target}.");
    return 2;
}

XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var document = XDocument.Load(report);
var classes = document.Descendants(ns + "UnitTest").ToDictionary(
    test => (string)test.Attribute("id")!,
    test => (string?)test.Element(ns + "TestMethod")?.Attribute("className") ?? "?");
var results = document.Descendants(ns + "UnitTestResult")
    .Select(result => (
        Name: (string)result.Attribute("testName")!,
        Class: classes.GetValueOrDefault((string)result.Attribute("testId")!, "?"),
        Duration: TimeSpan.Parse((string?)result.Attribute("duration") ?? "0", CultureInfo.InvariantCulture),
        Start: DateTimeOffset.Parse((string?)result.Attribute("startTime") ?? "0001-01-01", CultureInfo.InvariantCulture),
        End: DateTimeOffset.Parse((string?)result.Attribute("endTime") ?? "0001-01-01", CultureInfo.InvariantCulture)))
    .ToList();
var first = results.Min(result => result.Start);
var wall = results.Max(result => result.End) - first;

var total = results.Aggregate(TimeSpan.Zero, (sum, result) => sum + result.Duration);
Console.WriteLine($"{Path.GetFileName(report)}: {results.Count} tests, {total.TotalSeconds:F1} s summed, " +
    $"{wall.TotalSeconds:F1} s from the first start to the last end");

Console.WriteLine();
Console.WriteLine($"Slowest {top} tests (duration, then start offset from the first test)");
foreach (var result in results.OrderByDescending(result => result.Duration).Take(top))
    Console.WriteLine($"{result.Duration.TotalSeconds,8:F2} s  @{(result.Start - first).TotalSeconds,6:F1} s  {result.Name}");

Console.WriteLine();
Console.WriteLine($"Slowest {top} classes (summed)");
foreach (var group in results.GroupBy(result => result.Class)
             .Select(group => (Class: group.Key, Count: group.Count(),
                 Duration: group.Aggregate(TimeSpan.Zero, (sum, result) => sum + result.Duration)))
             .OrderByDescending(group => group.Duration).Take(top))
    Console.WriteLine($"{group.Duration.TotalSeconds,8:F2} s  {group.Count,4} tests  {group.Class}");

return 0;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
