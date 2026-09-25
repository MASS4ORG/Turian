namespace Turian.Editor.Core;

/// <summary>
/// Filters, deduplicates, and logs Roslyn compilation diagnostics.
/// </summary>
public static class CompilationDiagnosticsReporter
{
    /// <summary>
    /// Reports all relevant diagnostics (Warning and above) through the logger, skipping hidden/info
    /// noise and collapsing exact duplicates.
    /// </summary>
    /// <returns>Tuple of (errorCount, warningCount) after deduplication.</returns>
    public static (int errorCount, int warningCount) Report(
        ImmutableArray<Diagnostic> diagnostics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var relevant = diagnostics
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .DistinctBy(static d => DiagnosticKey(d))
            .OrderBy(static d => d.Location.IsInSource ? d.Location.GetLineSpan().Path : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static d => d.Location.IsInSource ? d.Location.GetLineSpan().StartLinePosition.Line : -1)
            .ToList();

        foreach (var d in relevant)
        {
            var (file, line, col) = GetSourceLocation(d);
            var message = d.GetMessage(CultureInfo.InvariantCulture);

            if (d.Severity == DiagnosticSeverity.Error)
                logger.LogError("{File}({Line},{Column}): error {Code}: {Message}", file, line, col, d.Id, message);
            else
                logger.LogWarning("{File}({Line},{Column}): warning {Code}: {Message}", file, line, col, d.Id, message);
        }

        return (
            relevant.Count(static d => d.Severity == DiagnosticSeverity.Error),
            relevant.Count(static d => d.Severity == DiagnosticSeverity.Warning));
    }

    /// <summary>
    /// Formats a single diagnostic as <c>file(line,col): severity code: message</c>.
    /// </summary>
    public static string FormatMessage(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var (file, line, col) = GetSourceLocation(diagnostic);
        var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);

        return $"{file}({line},{col}): {severity} {diagnostic.Id}: {message}";
    }

    static string DiagnosticKey(Diagnostic d)
    {
        var (file, line, col) = GetSourceLocation(d);
        return $"{file}|{line}|{col}|{d.Id}|{d.GetMessage(CultureInfo.InvariantCulture)}";
    }

    static (string file, int line, int col) GetSourceLocation(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        if (string.IsNullOrEmpty(span.Path))
            return ("N/A", 0, 0);

        return (
            span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }
}
