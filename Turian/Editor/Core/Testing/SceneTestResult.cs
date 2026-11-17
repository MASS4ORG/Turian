namespace Turian.Editor.Core;

/// <summary>The outcome displayed by test tooling.</summary>
public enum SceneTestStatus
{
    /// <summary>A discovered test that has not run.</summary>
    NotRun,
    /// <summary>The test and its cleanup completed successfully.</summary>
    Passed,
    /// <summary>The test, its signature or its cleanup failed.</summary>
    Failed,
}

/// <summary>A test's stable name, outcome and source location, independent of loaded user types.</summary>
public sealed record SceneTestResult(string Name, SceneTestStatus Status, double Seconds = 0,
    string? Message = null, string? StackTrace = null, string? SourceFile = null, int SourceLine = 0);

/// <summary>The complete results of one discovery or test run.</summary>
public sealed record SceneTestReport(IReadOnlyList<SceneTestResult> Tests)
{
    /// <summary>Whether a nonempty selection ran without failures.</summary>
    public bool Succeeded => Tests.Count > 0 && Tests.All(static test => test.Status == SceneTestStatus.Passed);
}
