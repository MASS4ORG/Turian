#:property PublishAot=false
#:property GenerateDocumentationFile=true

using System.Text.Json;

if (args.Length is < 1 or > 2 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run .analyzers/summarize.cs <SARIF directory> [output.txt]");
    return 1;
}

var files = Directory.GetFiles(args[0], "*.sarif", SearchOption.AllDirectories);
if (files.Length == 0)
{
    Console.Error.WriteLine("No analyzer SARIF files were produced.");
    return 1;
}

var occurrences = new Dictionary<(string Id, string Uri, int Line, int Column), string>();
var severityRanks = new Dictionary<string, int> { ["none"] = 0, ["note"] = 1, ["warning"] = 2, ["error"] = 3 };
var suppressedCount = 0;
foreach (var file in files)
{
    using var document = JsonDocument.Parse(File.ReadAllText(file));
    foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
    {
        if (!run.TryGetProperty("results", out var results))
        {
            continue;
        }
        foreach (var result in results.EnumerateArray())
        {
            if (result.TryGetProperty("suppressions", out var suppressions) &&
                suppressions.EnumerateArray().Any(suppression =>
                    !suppression.TryGetProperty("status", out var status) || status.GetString() == "accepted"))
            {
                suppressedCount++;
                continue;
            }
            var id = result.GetProperty("ruleId").GetString()!;
            var level = result.TryGetProperty("level", out var severity) ? severity.GetString()! : "warning";
            var uri = file;
            var line = 0;
            var column = 0;
            if (result.TryGetProperty("locations", out var locations) && locations.GetArrayLength() > 0)
            {
                var physical = locations[0].GetProperty("physicalLocation");
                if (physical.TryGetProperty("artifactLocation", out var artifact))
                {
                    uri = artifact.GetProperty("uri").GetString()!;
                }
                if (physical.TryGetProperty("region", out var region))
                {
                    line = region.TryGetProperty("startLine", out var startLine) ? startLine.GetInt32() : 0;
                    column = region.TryGetProperty("startColumn", out var startColumn) ? startColumn.GetInt32() : 0;
                }
            }
            var key = (id, uri, line, column);
            if (!occurrences.TryGetValue(key, out var existing) || severityRanks[level] > severityRanks[existing])
            {
                occurrences[key] = level;
            }
        }
    }
}

var output = new List<string>
{
    $"SARIF files: {files.Length}; unique locations: {occurrences.Count}; suppressed records: {suppressedCount}",
    $"{"DiagnosticId",-34} {"Count",6} {"Info",6} {"Warning",7} {"Error",6}"
};
foreach (var group in occurrences.GroupBy(entry => entry.Key.Id)
             .OrderByDescending(group => group.Count()).ThenBy(group => group.Key))
{
    output.Add($"{group.Key,-34} {group.Count(),6} " +
        $"{group.Count(entry => entry.Value == "note"),6} " +
        $"{group.Count(entry => entry.Value == "warning"),7} " +
        $"{group.Count(entry => entry.Value == "error"),6}");
}
var report = string.Join(Environment.NewLine, output) + Environment.NewLine;
Console.Write(report);
if (args.Length == 2)
{
    File.WriteAllText(args[1], report);
}
return 0;
