using Turian.Testing;

namespace Turian.Tests;

/// <summary>Checks discovery, per-test isolation, failures, source locations and JUnit output.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneTestRunnerTests
{
    sealed class SceneRunnerFixtures : IDisposable
    {
        public static int Disposals { get; set; }
        [SceneTest] public void A_Passes(TestScene scene) => SceneAssert.Equal(0, scene.Root.Children.Count);
        [SceneTest] public void B_Fails() => SceneAssert.True(false, "failure < & >");
        [SceneTest] public int C_InvalidSignature() => 0;
        [SceneTest] public async void D_InvalidAsyncVoid() => await Task.Yield();
        public void Dispose() => Disposals++;
    }

    static SceneTestReport Run(string? filter = null, bool listOnly = false) =>
        SceneTestRunner.Run([typeof(SceneTestRunnerTests).Assembly],
            filter ?? nameof(SceneRunnerFixtures), listOnly, TestContext.Current.CancellationToken);

    /// <summary>Discovery is deterministic, applies filters and never constructs or runs fixtures.</summary>
    [Fact]
    public void ListsAndFiltersWithoutExecuting()
    {
        SceneRunnerFixtures.Disposals = 0;
        var report = Run(listOnly: true);
        Assert.Equal(4, report.Tests.Count);
        Assert.All(report.Tests, static test => Assert.Equal(SceneTestStatus.NotRun, test.Status));
        Assert.Equal(0, SceneRunnerFixtures.Disposals);
        Assert.Single(Run("a_passes", listOnly: true).Tests);
        Assert.Empty(Run("no-such-test").Tests);
        Assert.False(new SceneTestReport([]).Succeeded);
    }

    /// <summary>Failures retain their source line and do not suppress later tests; fixture cleanup always runs.</summary>
    [Fact]
    public void RunsEverySelectedTestAndReportsFailures()
    {
        SceneRunnerFixtures.Disposals = 0;
        var report = Run();
        Assert.Equal(SceneTestStatus.Passed, report.Tests[0].Status);
        Assert.Equal(3, report.Tests.Count(static test => test.Status == SceneTestStatus.Failed));
        Assert.Equal(2, SceneRunnerFixtures.Disposals);
        var failure = report.Tests[1];
        Assert.Contains("failure < & >", failure.Message);
        Assert.EndsWith("SceneTestRunnerTests.cs", failure.SourceFile);
        Assert.True(failure.SourceLine > 0);
        Assert.Contains("SceneAssertionException", failure.StackTrace);
        Assert.False(report.Succeeded);
        Assert.True(Run("A_Passes").Succeeded);
    }

    /// <summary>Cancellation prevents the first fixture from starting.</summary>
    [Fact]
    public void HonorsCancellation() => Assert.Throws<OperationCanceledException>(static () =>
        SceneTestRunner.Run([typeof(SceneTestRunnerTests).Assembly], cancellationToken: new(true)));

    /// <summary>JUnit preserves escaping, source navigation, skipped discovery and invariant durations.</summary>
    [Fact]
    public void WritesJUnit()
    {
        var directory = Directory.CreateTempSubdirectory("turian-junit-");
        try
        {
            var path = Path.Combine(directory.FullName, "nested", "results.xml");
            var report = Run();
            SceneTestJUnit.Write(report, path);
            var suite = XDocument.Load(path).Root!.Element("testsuite")!;
            Assert.Equal("4", suite.Attribute("tests")!.Value);
            Assert.Equal("3", suite.Attribute("failures")!.Value);
            Assert.Contains("failure < & >", suite.Elements("testcase").ElementAt(1).Element("failure")!
                .Attribute("message")!.Value);
            SceneTestJUnit.Write(Run(listOnly: true), path);
            Assert.Equal(4, XDocument.Load(path).Descendants("skipped").Count());
            SceneTestJUnit.Write(new SceneTestReport([new SceneTestResult("ZeroByte", SceneTestStatus.Failed,
                Message: "Expected \0; received a", StackTrace: "\u0001")]), path);
            var failure = XDocument.Load(path).Descendants("failure").Single();
            Assert.Equal("Expected \\u0000; received a", failure.Attribute("message")!.Value);
            Assert.Equal("\\u0001", failure.Value);
        }
        finally { directory.Delete(true); }
    }
}
