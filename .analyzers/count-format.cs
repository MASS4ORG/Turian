#:property PublishAot=false
#:property GenerateDocumentationFile=true

using System.Text.Json;
using System.Text.RegularExpressions;

var uniqueDesc = false;
var showLocations = false;
var severityFilter = "hidden";
string? inputPath = null;
string? root = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--unique-desc": uniqueDesc = true; break;
        case "--locations": showLocations = true; break;
        case "--severity" when i + 1 < args.Length: severityFilter = args[++i].ToLowerInvariant(); break;
        case "--root" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal) || inputPath is not null)
            {
                Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                return 1;
            }
            inputPath = args[i];
            break;
    }
}

var severityRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
    ["hidden"] = 0, ["info"] = 1, ["warning"] = 2, ["error"] = 3
};
if (inputPath is null || (severityFilter != "all" && !severityRanks.ContainsKey(severityFilter)))
{
    Console.Error.WriteLine("Usage: dotnet run <script.cs> <report.json> " +
        "[--severity hidden|info|warning|error|all] [--root <directory>] [--unique-desc] [--locations]");
    return 1;
}

using var document = JsonDocument.Parse(File.ReadAllText(inputPath));
var occurrences = new Dictionary<(string Id, string Path, int Line, int Column),
    (string Severity, string Description)>();
var rawCount = 0;
foreach (var item in document.RootElement.EnumerateArray())
{
    if (!item.TryGetProperty("FileChanges", out var changes) || changes.ValueKind != JsonValueKind.Array)
    {
        continue;
    }

    var filePath = GetString(item, "FilePath");
    if (string.IsNullOrWhiteSpace(filePath))
    {
        throw new InvalidDataException("Report entry has no FilePath; cannot deduplicate locations.");
    }
    filePath = Path.GetFullPath(filePath, Path.GetDirectoryName(Path.GetFullPath(inputPath))!);
    if (root is not null)
    {
        var relative = Path.GetRelativePath(root, filePath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            continue;
        }
    }

    foreach (var change in changes.EnumerateArray())
    {
        var id = GetString(change, "DiagnosticId");
        if (string.IsNullOrWhiteSpace(id))
        {
            continue;
        }
        var match = Regex.Match(GetString(change, "FormatDescription"),
            @"^\s*(hidden|info|warning|error)\s+" + Regex.Escape(id) + @":\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException($"Unknown severity for {id} in {filePath}.");
        }
        var severity = match.Groups[1].Value.ToLowerInvariant();
        if (severityFilter != "all" && severity != severityFilter)
        {
            continue;
        }
        var key = (id, filePath, change.GetProperty("LineNumber").GetInt32(),
            change.GetProperty("CharNumber").GetInt32());
        rawCount++;
        // Shared source can appear in multiple projects; retain its highest reported severity.
        if (!occurrences.TryGetValue(key, out var existing) ||
            severityRanks[severity] > severityRanks[existing.Severity])
        {
            occurrences[key] = (severity, match.Groups[2].Value.Trim());
        }
    }
}

Console.WriteLine($"Severity: {severityFilter}; records: {rawCount}; " +
    $"unique locations: {occurrences.Count}; duplicates removed: {rawCount - occurrences.Count}");
Console.WriteLine($"{"DiagnosticId",-34} {"Count",6} {"Hidden",6} {"Info",6} {"Warning",7} {"Error",6}" +
    (uniqueDesc ? "  Description" : ""));
foreach (var group in occurrences.GroupBy(entry =>
             (entry.Key.Id, Description: uniqueDesc ? entry.Value.Description : ""))
         .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Id)
         .ThenBy(group => group.Key.Description))
{
    Console.WriteLine($"{group.Key.Id,-34} {group.Count(),6} " +
        $"{group.Count(entry => entry.Value.Severity == "hidden"),6} " +
        $"{group.Count(entry => entry.Value.Severity == "info"),6} " +
        $"{group.Count(entry => entry.Value.Severity == "warning"),7} " +
        $"{group.Count(entry => entry.Value.Severity == "error"),6}" +
        (uniqueDesc ? "  " + group.Key.Description : ""));
}
if (showLocations)
{
    foreach (var entry in occurrences.OrderBy(entry => entry.Key.Id).ThenBy(entry => entry.Key.Path)
             .ThenBy(entry => entry.Key.Line).ThenBy(entry => entry.Key.Column))
    {
        Console.WriteLine($"{entry.Key.Id} {entry.Value.Severity} " +
            $"{entry.Key.Path}:{entry.Key.Line}:{entry.Key.Column} {entry.Value.Description}");
    }
}
return 0;

static string GetString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : "";
