namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command TestCommand(Argument<FileSystemInfo> projectArg)
    {
        var filter = new Option<string?>("--filter") { Description = "Case-insensitive test-name substring" };
        var list = new Option<bool>("--list") { Description = "Discover tests without executing them" };
        var results = new Option<FileInfo?>("--results")
        {
            Description = "JUnit XML destination (default: <project>/TestResults/results.xml)",
        };
        var command = new Command("test", "Build and run component tests headlessly")
        {
            projectArg, filter, list, results,
        };
        command.SetAction(async (parsed, cancellationToken) =>
        {
            var settings = CreateSettings(parsed.GetValue(projectArg)!);
            var listOnly = parsed.GetValue(list);
            var report = await new ProjectTestRunner(Log.Logger).RunAsync(settings, parsed.GetValue(filter),
                listOnly, cancellationToken).ConfigureAwait(false);
            PrintTestReport(report);
            var output = parsed.GetValue(results)?.FullName
                ?? Path.Combine(settings.ProjectAbsoluteDir, "TestResults", "results.xml");
            SceneTestJUnit.Write(report, output);
            var succeeded = listOnly ? !report.Tests.Any(static test => test.Status == SceneTestStatus.Failed)
                : report.Succeeded;
            return succeeded ? 0 : 1;
        });
        return command;
    }

    static void PrintTestReport(SceneTestReport report)
    {
        foreach (var test in report.Tests)
        {
            Console.WriteLine($"{test.Status}: {test.Name}");
            if (test.Message is not null) { Console.Error.WriteLine(test.Message); }
            if (test.SourceFile is not null)
            {
                Console.WriteLine($"  {test.SourceFile}({test.SourceLine}):");
            }
        }
        if (report.Tests.Count == 0) { Console.Error.WriteLine("No matching component tests were found."); }
    }
}
