namespace Turian.Tests;

/// <summary>Registers the installed build tools before tests load MSBuild project types.</summary>
static class TestMsBuild
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
}
