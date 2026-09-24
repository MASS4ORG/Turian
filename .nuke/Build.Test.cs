namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the build process.
/// </summary>
sealed partial class Build
{
    AbsolutePath TestProjectDirectory => Solution.Turian_Tests.Directory;
    static AbsolutePath CoverageDirectory => RootDirectory / "coverage";
    static AbsolutePath CoverageResultFile => CoverageDirectory / "coverage.xml";
    static AbsolutePath CoverageReportDirectory => CoverageDirectory / "report";
    static AbsolutePath CoverageReportSummaryDirectory => CoverageReportDirectory / "Summary.txt";
    AbsolutePath CoverageSettingsFile => TestProjectDirectory / "CodeCoverage.runsettings";

    Target Test => td =>
        td
            .After(Restore, Compile)
            .Produces(CoverageResultFile)
            .Executes(() =>
            {
                DotNetRun(settings => settings
                    .SetConfiguration(Configuration)
                    .SetProjectFile(Solution.Turian_Tests.Path)
                    .SetApplicationArguments(
                        "--coverage",
                        "--coverage-settings", CoverageSettingsFile, // Excludes source generated files
                        "--coverage-output-format", "cobertura",
                        "--coverage-output", CoverageResultFile)
                );
            });

    public Target TestReport => td =>
        td
            .DependsOn(Test)
            .Consumes(Test, CoverageResultFile)
            .Executes(() =>
            {
                _ = CoverageReportDirectory.CreateDirectory();
                _ = ReportGenerator(
                    s => s
                         .SetTargetDirectory(CoverageReportDirectory)
                         .SetReportTypes([ReportTypes.Html, ReportTypes.TextSummary])
                         .SetReports(CoverageResultFile)
                );
                var summaryText = CoverageReportSummaryDirectory.ReadAllLines();
                Log.Information("{Summary}", string.Join(Environment.NewLine, summaryText));
            });
}
