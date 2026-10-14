namespace Turian.Tests;

public sealed partial class DataAssetGeneratorTests
{
    /// <summary>Methods, local functions, lambdas and anonymous delegates cannot escape gameplay observation.</summary>
    [Theory]
    [InlineData("public async void Unsafe() { await Task.Yield(); }")]
    [InlineData("public void Unsafe() { async void Local() { await Task.Yield(); } Local(); }")]
    [InlineData("public void Unsafe() { System.Action action = async () => await Task.Yield(); }")]
    [InlineData("public void Unsafe() { System.Action action = async delegate { await Task.Yield(); }; }")]
    public async Task GameplayAnalyzer_RejectsAsyncVoid(string member)
    {
        var diagnostics = await AnalyzeGameplay(member, true);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TUR0002" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Only component code receives gameplay-specific warnings.</summary>
    [Theory]
    [InlineData("public Task Unsafe() => Task.Delay(1);", "TUR0003")]
    [InlineData("public ValueTask Stored;", "TUR0004")]
    [InlineData("public ValueTask<int> Stored;", "TUR0004")]
    public async Task GameplayAnalyzer_FlagsUnsafePatternsOnlyInComponents(string member, string id)
    {
        Assert.Contains(await AnalyzeGameplay(member, true), diagnostic => diagnostic.Id == id);
        Assert.Empty(await AnalyzeGameplay(member, false));
    }

    /// <summary>Observed ValueTask methods and Task fields remain valid gameplay code.</summary>
    [Fact]
    public async Task GameplayAnalyzer_AllowsObservedResults()
        => Assert.Empty(await AnalyzeGameplay("public Task Stored; public async ValueTask Safe() " +
            "{ await Async.NextFrame(); }", true));

    static Task<ImmutableArray<Diagnostic>> AnalyzeGameplay(string member, bool component)
    {
        var source = "using System.Threading.Tasks; using Turian.Engine.Core; public class Script" +
            (component ? " : Component" : "") + " { " + member + " }";
        return Compile(source).WithAnalyzers([LoadFromGenerator<DiagnosticAnalyzer>(
                "Turian.CSharp.CodeGenerator.GameplayAsyncAnalyzer")])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }
}
