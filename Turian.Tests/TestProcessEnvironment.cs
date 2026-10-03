[assembly: AssemblyFixture(typeof(Turian.Tests.TestProcessEnvironment))]

namespace Turian.Tests;

/// <summary>
/// Keeps builds started by tests from outliving the run: MSBuild worker nodes and the shared compiler server are
/// turned off for this process and every child it starts, so no idle dotnet processes are left behind.
/// </summary>
public sealed class TestProcessEnvironment
{
    /// <summary>Sets the build environment before the first test runs.</summary>
    public TestProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");
    }
}
