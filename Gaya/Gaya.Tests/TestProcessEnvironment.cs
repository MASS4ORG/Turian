[assembly: AssemblyFixture(typeof(Gaya.Tests.TestProcessEnvironment))]

namespace Gaya.Tests;

/// <summary>
/// Isolates user preferences and the shared brick store for tests and their child processes.
/// </summary>
public sealed class TestProcessEnvironment
{
    /// <summary>Points the preference and brick stores at fresh temporary directories before the first test runs.</summary>
    public TestProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("GAYA_CONFIG_HOME",
            Directory.CreateTempSubdirectory("gaya-test-preferences-").FullName);
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable,
            Directory.CreateTempSubdirectory("gaya-test-bricks-").FullName);
    }
}
