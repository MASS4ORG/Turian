namespace Turian.Editor.Core;

public sealed partial class ProjectBootstrapper
{
    static async Task CreateTestTemplateAsync(string project)
    {
        var tests = Path.Combine(project, "Tests");
        Directory.CreateDirectory(tests);
        var sample = Path.Combine(tests, "SceneTests.cs");
        if (!File.Exists(sample))
        {
            await File.WriteAllTextAsync(sample, """
                using Turian.Testing;

                public static class SceneTests
                {
                    [SceneTest]
                    public static void StartsWithAnEmptyScene(TestScene scene)
                    {
                        SceneAssert.Equal(0, scene.Root.Children.Count);
                        scene.Step(3);
                        SceneAssert.Equal(3L, scene.Ticker.Clock.TickIndex);
                    }
                }
                """).ConfigureAwait(false);
        }
        var workflow = Path.Combine(project, ".github", "workflows", "component-tests.yml.example");
        Directory.CreateDirectory(Path.GetDirectoryName(workflow)!);
        if (File.Exists(workflow)) { return; }
        await File.WriteAllTextAsync(workflow, """
            name: Component tests
            on: [push, pull_request]
            jobs:
              test:
                runs-on: ubuntu-latest
                container: ghcr.io/mass4org/turian-cli:latest
                steps:
                  - uses: actions/checkout@v7
                  - run: turian-cli brick restore --locked --project .
                  - run: turian-cli test . --results TestResults/results.xml
                  - uses: actions/upload-artifact@v7
                    if: always()
                    with:
                      name: component-test-results
                      path: TestResults/*.xml
            """).ConfigureAwait(false);
    }
}
