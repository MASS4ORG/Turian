[assembly: AssemblyFixture(typeof(Turian.Tests.TestProcessEnvironment))]

namespace Turian.Tests;

/// <summary>
/// Isolates user preferences and disables reusable build servers for tests and their child processes.
/// </summary>
public sealed class TestProcessEnvironment
{
    /// <summary>Sets the build environment before the first test runs.</summary>
    public TestProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("GAYA_CONFIG_HOME",
            Directory.CreateTempSubdirectory("turian-test-preferences-").FullName);
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");
    }
}
