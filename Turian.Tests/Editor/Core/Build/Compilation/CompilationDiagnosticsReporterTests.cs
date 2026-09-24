#pragma warning disable RS2008 // RS2008: test-only DiagnosticDescriptors, not real analyzer rules

namespace Turian.Tests.Turian.Editor.Build;

/// <summary>
/// Tests for <see cref="CompilationDiagnosticsReporter"/>.
/// Verifies filtering, deduplication, formatting, and severity counting.
/// </summary>
public class CompilationDiagnosticsReporterTests
{
    static readonly DiagnosticDescriptor errorDescriptor = new(
        id: "TEST0001",
        title: "Test Error",
        messageFormat: "{0}",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor warningDescriptor = new(
        id: "TEST0002",
        title: "Test Warning",
        messageFormat: "{0}",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor infoDescriptor = new(
        id: "TEST0003",
        title: "Test Info",
        messageFormat: "{0}",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor hiddenDescriptor = new(
        id: "TEST0004",
        title: "Test Hidden",
        messageFormat: "{0}",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Hidden,
        isEnabledByDefault: true);

    readonly ILogger logger = Substitute.For<ILogger>();

    // ── FormatMessage ─────────────────────────────────────────────────────────

    /// <summary>Error diagnostic formats as file(line,col): error id: message.</summary>
    [Fact]
    public void FormatMessage_Error_ReturnsExpectedFormat()
    {
        var d = MakeDiagnostic(errorDescriptor, "TestFile.cs", line: 4, col: 8, "undeclared variable");

        var result = CompilationDiagnosticsReporter.FormatMessage(d);

        Assert.Equal("TestFile.cs(5,9): error TEST0001: undeclared variable", result);
    }

    /// <summary>Warning diagnostic uses the warning keyword in the format.</summary>
    [Fact]
    public void FormatMessage_Warning_ReturnsWarningKeyword()
    {
        var d = MakeDiagnostic(warningDescriptor, "Foo.cs", line: 0, col: 0, "unused import");

        var result = CompilationDiagnosticsReporter.FormatMessage(d);

        Assert.StartsWith("Foo.cs(1,1): warning TEST0002:", result, StringComparison.Ordinal);
    }

    /// <summary>Diagnostic with no source location shows N/A as the file path.</summary>
    [Fact]
    public void FormatMessage_NoSourceLocation_ShowsNA()
    {
        var d = Diagnostic.Create(errorDescriptor, Location.None, "no location");

        var result = CompilationDiagnosticsReporter.FormatMessage(d);

        Assert.StartsWith("N/A(0,0): error TEST0001:", result, StringComparison.Ordinal);
    }

    // ── Report — empty ────────────────────────────────────────────────────────

    /// <summary>Empty diagnostic list returns zero error and warning counts.</summary>
    [Fact]
    public void Report_EmptyDiagnostics_ReturnsZeroCounts()
    {
        var (errors, warnings) = CompilationDiagnosticsReporter.Report([], logger);

        Assert.Equal(0, errors);
        Assert.Equal(0, warnings);
    }

    // ── Report — filtering ────────────────────────────────────────────────────

    /// <summary>Hidden diagnostics are excluded from the report.</summary>
    [Fact]
    public void Report_HiddenDiagnostic_IsNotCounted()
    {
        var d = Diagnostic.Create(hiddenDescriptor, Location.None, "hidden");

        var (errors, warnings) = CompilationDiagnosticsReporter.Report([d], logger);

        Assert.Equal(0, errors);
        Assert.Equal(0, warnings);
    }

    /// <summary>Informational diagnostics are excluded from the report.</summary>
    [Fact]
    public void Report_InfoDiagnostic_IsNotCounted()
    {
        var d = Diagnostic.Create(infoDescriptor, Location.None, "info");

        var (errors, warnings) = CompilationDiagnosticsReporter.Report([d], logger);

        Assert.Equal(0, errors);
        Assert.Equal(0, warnings);
    }

    /// <summary>Error diagnostic is counted in the error total.</summary>
    [Fact]
    public void Report_ErrorDiagnostic_IsCountedAsError()
    {
        var d = Diagnostic.Create(errorDescriptor, Location.None, "error msg");

        var (errors, warnings) = CompilationDiagnosticsReporter.Report([d], logger);

        Assert.Equal(1, errors);
        Assert.Equal(0, warnings);
    }

    /// <summary>Warning diagnostic is counted in the warning total.</summary>
    [Fact]
    public void Report_WarningDiagnostic_IsCountedAsWarning()
    {
        var d = Diagnostic.Create(warningDescriptor, Location.None, "warn msg");

        var (errors, warnings) = CompilationDiagnosticsReporter.Report([d], logger);

        Assert.Equal(0, errors);
        Assert.Equal(1, warnings);
    }

    // ── Report — deduplication ────────────────────────────────────────────────

    /// <summary>Two identical diagnostics at the same location are deduplicated to one.</summary>
    [Fact]
    public void Report_DuplicateErrorAtSameLocation_CountedOnce()
    {
        var d1 = MakeDiagnostic(errorDescriptor, "File.cs", line: 2, col: 4, "same error");
        var d2 = MakeDiagnostic(errorDescriptor, "File.cs", line: 2, col: 4, "same error");

        var (errors, _) = CompilationDiagnosticsReporter.Report([d1, d2], logger);

        Assert.Equal(1, errors);
    }

    /// <summary>Same error code at different locations are both reported.</summary>
    [Fact]
    public void Report_SameCodeDifferentLocations_BothCounted()
    {
        var d1 = MakeDiagnostic(errorDescriptor, "File.cs", line: 2, col: 4, "error msg");
        var d2 = MakeDiagnostic(errorDescriptor, "File.cs", line: 5, col: 1, "error msg");

        var (errors, _) = CompilationDiagnosticsReporter.Report([d1, d2], logger);

        Assert.Equal(2, errors);
    }

    /// <summary>Same location with different messages are both reported.</summary>
    [Fact]
    public void Report_SameLocationDifferentMessages_BothCounted()
    {
        var d1 = MakeDiagnostic(errorDescriptor, "File.cs", line: 2, col: 4, "first error");
        var d2 = MakeDiagnostic(errorDescriptor, "File.cs", line: 2, col: 4, "second error");

        var (errors, _) = CompilationDiagnosticsReporter.Report([d1, d2], logger);

        Assert.Equal(2, errors);
    }

    // ── Report — mixed severity ───────────────────────────────────────────────

    /// <summary>Mixed severity list: only errors and warnings are counted; hidden and info are excluded.</summary>
    [Fact]
    public void Report_MixedSeverity_ReturnsCorrectCounts()
    {
        var diagnostics = ImmutableArray.Create(
            Diagnostic.Create(errorDescriptor, Location.None, "e1"),
            Diagnostic.Create(errorDescriptor, Location.None, "e2"),
            Diagnostic.Create(warningDescriptor, Location.None, "w1"),
            Diagnostic.Create(infoDescriptor, Location.None, "i1"),
            Diagnostic.Create(hiddenDescriptor, Location.None, "h1"));

        var (errors, warnings) = CompilationDiagnosticsReporter.Report(diagnostics, logger);

        Assert.Equal(2, errors);
        Assert.Equal(1, warnings);
    }

    // ── Report — logger calls ─────────────────────────────────────────────────

    /// <summary>Error diagnostic causes a LogError call, not LogWarning.</summary>
    [Fact]
    public void Report_ErrorDiagnostic_LogsAsError()
    {
        var d = Diagnostic.Create(errorDescriptor, Location.None, "error msg");
        CompilationDiagnosticsReporter.Report([d], logger);

        var levels = LoggedLevels();
        Assert.Contains(LogLevel.Error, levels);
        Assert.DoesNotContain(LogLevel.Warning, levels);
    }

    /// <summary>Warning diagnostic causes a LogWarning call, not LogError.</summary>
    [Fact]
    public void Report_WarningDiagnostic_LogsAsWarning()
    {
        var d = Diagnostic.Create(warningDescriptor, Location.None, "warn msg");
        CompilationDiagnosticsReporter.Report([d], logger);

        var levels = LoggedLevels();
        Assert.DoesNotContain(LogLevel.Error, levels);
        Assert.Contains(LogLevel.Warning, levels);
    }

    /// <summary>
    /// The level of every logged entry. <see cref="ILogger"/>'s <c>LogError</c>/<c>LogWarning</c>
    /// extension methods all forward to the single <c>Log</c> interface method, so the level has to
    /// be read from its first argument rather than from the received method's name.
    /// </summary>
    List<LogLevel> LoggedLevels() =>
        [.. logger.ReceivedCalls().Select(c => (LogLevel)c.GetArguments()[0]!)];

    // ── Helpers ───────────────────────────────────────────────────────────────

    static Diagnostic MakeDiagnostic(
        DiagnosticDescriptor descriptor,
        string filePath,
        int line,
        int col,
        string message)
    {
        var start = new LinePosition(line, col);
        var location = Location.Create(
            filePath,
            TextSpan.FromBounds(0, 0),
            new LinePositionSpan(start, start));

        return Diagnostic.Create(descriptor, location, message);
    }
}
