[assembly: AssemblyFixture(typeof(Gaya.Tests.TestProcessEnvironment))]

namespace Gaya.Tests;

/// <summary>
/// Isolates user preferences for tests and their child processes.
/// </summary>
public sealed class TestProcessEnvironment
{
    /// <summary>Points the preference store at a fresh temporary directory before the first test runs.</summary>
    public TestProcessEnvironment() =>
        Environment.SetEnvironmentVariable("GAYA_CONFIG_HOME",
            Directory.CreateTempSubdirectory("gaya-test-preferences-").FullName);
}
