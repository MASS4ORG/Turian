namespace Turian.Tests;

/// <summary>Runs real project tests through the CLI, including compilation, source symbols and CI exit codes.</summary>
[Trait("Category", "E2E")]
[Collection(SerialTests.Name)]
public sealed class ComponentTestCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-component-cli-").FullName;

    /// <inheritdoc />
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>A scaffold's tests run without graphics, discover without execution and fail CI on a failure.</summary>
    [Fact]
    public async Task RunsListsFiltersAndReportsFailures()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "sample")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        var module = Directory.CreateDirectory(Path.Combine(project, "Assets", "Module")).FullName;
        await File.WriteAllTextAsync(Path.Combine(module, "Module.dataasset"), $$"""
            { "__TypeId": "{{AssemblyDefinition.TypeIdValue}}", "Name": "Sample.Module", "AutoReferenced": false,
              "NoEngineReferences": true, "Id": "fe1b472b-48e4-4568-9a17-8ee43a916d80" }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(module, "Module.dataasset.meta"), """
            { "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c", "RelativePath": "Assets/Module/Module.dataasset",
              "Id": "fe1b472b-48e4-4568-9a17-8ee43a916d80" }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(module, "Value.cs"),
            "namespace Sample.Module; public static class Value { public static int Expected => 7; }",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(project, "Assets", "Counter.cs"), """
            using System.Text.Json.Serialization;
            namespace Usercode;
            public sealed class Counter : Component
            {
                [InjectService, JsonIgnore] public IInputSource Input { get; private set; } = null!;
                public int Presses { get; private set; }
                public override void OnUpdate(float deltaTime)
                {
                    if (Input.WasKeyPressed(Silk.NET.Input.Key.Space)) { Presses++; }
                }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(project, "Tests", "InputTests.cs"), """
            using System.Runtime.InteropServices;
            using Silk.NET.Input;
            using Turian.Testing;
            public static class InputTests
            {
                [DllImport("libc", EntryPoint = "getpid")] private static extern int UnixProcessId();
                [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")] private static extern int WindowsProcessId();

                [SceneTest] public static void SupportsNativeComponents() =>
                    SceneAssert.Equal(Environment.ProcessId, OperatingSystem.IsWindows() ? WindowsProcessId() : UnixProcessId());

                [SceneTest] public static void ConsumesOnePress(TestScene scene)
                {
                    var counter = scene.Add<Usercode.Counter>();
                    scene.Input.PushKeyDown(Key.Space);
                    scene.Step(3);
                    SceneAssert.Equal(1, counter.Presses);
                    SceneAssert.Equal(7, Sample.Module.Value.Expected);
                }
            }
            """, TestContext.Current.CancellationToken);
        var results = Path.Combine(root, "reports", "tests.xml");
        var passing = await PrecastBrickFixture.RunCli(root, "test", project, "--results", results);
        Assert.True(passing.ExitCode == 0, passing.Output);
        Assert.Contains("Passed: SceneTests.StartsWithAnEmptyScene", passing.Output);
        Assert.Contains("Passed: InputTests.ConsumesOnePress", passing.Output);
        Assert.True(File.Exists(Path.Combine(project, ".Cache", "Tests", "bin", "Sample.Module.dll")));
        Assert.Equal("0", XDocument.Load(results).Descendants("testsuite").Single().Attribute("failures")!.Value);

        var testSource = Path.Combine(project, "Tests", "SceneTests.cs");
        await File.WriteAllTextAsync(testSource, """
            using Turian.Testing;
            public static class SceneTests
            {
                [SceneTest] public static void Fails(TestScene scene) => SceneAssert.True(false, "broken expectation");
            }
            """, TestContext.Current.CancellationToken);
        var listed = await PrecastBrickFixture.RunCli(root, "test", project, "--list");
        Assert.True(listed.ExitCode == 0, listed.Output);
        Assert.Contains("NotRun: SceneTests.Fails", listed.Output);
        Assert.DoesNotContain("broken expectation", listed.Output);
        var failing = await PrecastBrickFixture.RunCli(root, "test", project, "--filter", "fails", "--results", results);
        Assert.Equal(1, failing.ExitCode);
        Assert.Contains("broken expectation", failing.Output);
        Assert.Contains("SceneTests.cs(4)", failing.Output);
        Assert.Single(XDocument.Load(results).Descendants("failure"));
        var empty = await PrecastBrickFixture.RunCli(root, "test", project, "--filter", "no-match");
        Assert.Equal(1, empty.ExitCode);
        Assert.Contains("No matching", empty.Output);
    }

    /// <summary>Bad test code produces a failing report, and test sources never enter exported user assemblies.</summary>
    [Fact]
    public async Task CompilationFailuresAreReportedWithoutShippingTests()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "sample")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        await File.WriteAllTextAsync(Path.Combine(project, "Tests", "Broken.cs"), "invalid C#",
            TestContext.Current.CancellationToken);
        var result = await PrecastBrickFixture.RunCli(root, "test", project);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("(test build)", result.Output);
        Assert.Single(XDocument.Load(Path.Combine(project, "TestResults", "results.xml")).Descendants("failure"));
        var compile = await PrecastBrickFixture.RunCli(root, "compile", project);
        Assert.True(compile.ExitCode == 0, compile.Output);
        Assert.True(File.Exists(Path.Combine(project, ".github", "workflows", "component-tests.yml.example")));
    }
}
