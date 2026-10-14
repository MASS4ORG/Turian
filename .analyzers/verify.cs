#:property PublishAot=false
#:property GenerateDocumentationFile=true

using System.Diagnostics;
using System.Text.Json;

var temporary = Path.Combine(Path.GetTempPath(), "mass4-analyzers-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var scope = Path.Combine(temporary, "source");
    Directory.CreateDirectory(scope);
    var source = Path.Combine(scope, "sample.cs");
    var formatterReport = Path.Combine(temporary, "format.json");
    File.WriteAllText(formatterReport, JsonSerializer.Serialize(new[]
    {
        FormatEntry(source, "hidden", 10, "first"),
        FormatEntry(source, "hidden", 10, "duplicate with another description"),
        FormatEntry(source, "warning", 10, "higher severity"),
        FormatEntry(source, "hidden", 11, "second location"),
        FormatEntry(Path.Combine(temporary, "external.cs"), "info", 10, "external")
    }));

    Check(Run(".analyzers/count-format.cs", formatterReport),
        "records: 3; unique locations: 2; duplicates removed: 1");
    Check(Run(".analyzers/count-format.cs", formatterReport, "--severity", "all"),
        "records: 5; unique locations: 3; duplicates removed: 2");
    Check(Run(".analyzers/count-format.cs", formatterReport, "--severity", "all", "--root", scope,
            "--unique-desc", "--locations"),
        "records: 4; unique locations: 2; duplicates removed: 2");
    Check(Run(".analyzers/count-format.cs", formatterReport, "--severity", "warning"),
        "records: 1; unique locations: 1; duplicates removed: 0");

    var sarifDirectory = Path.Combine(temporary, "sarif");
    Directory.CreateDirectory(sarifDirectory);
    File.WriteAllText(Path.Combine(sarifDirectory, "one.sarif"), JsonSerializer.Serialize(new
    {
        runs = new[] { new { results = new[]
        {
            SarifEntry(source, "IDE0011", "note", 10, false),
            SarifEntry(source, "CA5350", "warning", 20, true)
        } } }
    }));
    File.WriteAllText(Path.Combine(sarifDirectory, "two.sarif"), JsonSerializer.Serialize(new
    {
        runs = new[] { new { results = new[]
        {
            SarifEntry(source, "IDE0011", "warning", 10, false),
            SarifEntry(source, "IDE0011", "warning", 11, false)
        } } }
    }));
    var summaryPath = Path.Combine(temporary, "occurrences.txt");
    var summary = Run(".analyzers/summarize.cs", sarifDirectory, summaryPath);
    Check(summary, "unique locations: 2; suppressed records: 1");
    if (File.ReadAllText(summaryPath) != summary)
    {
        throw new InvalidOperationException("The saved occurrence table differs from console output.");
    }
    Console.WriteLine("Analyzer reporting regression checks passed.");
}
finally
{
    Directory.Delete(temporary, true);
}

static object FormatEntry(string path, string severity, int line, string description) => new
{
    FilePath = path,
    FileChanges = new[] { new { DiagnosticId = "IDE0011", LineNumber = line, CharNumber = 3,
        FormatDescription = $"{severity} IDE0011: {description}" } }
};

static object SarifEntry(string path, string id, string level, int line, bool suppressed) => new
{
    ruleId = id,
    level,
    suppressions = suppressed ? new[] { new { status = "accepted", kind = "inSource" } } : [],
    locations = new[] { new { physicalLocation = new {
        artifactLocation = new { uri = new Uri(path).AbsoluteUri },
        region = new { startLine = line, startColumn = 3 } } } }
};

static string Run(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("run");
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(output + error);
    }
    return output;
}

static void Check(string output, string expected)
{
    if (!output.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}' in:\n{output}");
    }
}
