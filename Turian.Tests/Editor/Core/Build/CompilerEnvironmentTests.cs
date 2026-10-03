namespace Turian.Tests;

/// <summary>Tests the environment child <c>dotnet</c> processes are started with.</summary>
public class CompilerEnvironmentTests
{
    /// <summary>The MSBuild paths MSBuildLocator sets are dropped; everything else is passed on.</summary>
    [Fact]
    public void WithoutLocatorEnvironment_DropsOnlyLocatorPaths()
    {
        var start = new ProcessStartInfo("dotnet");
        start.Environment["MSBUILD_EXE_PATH"] = "/sdk/10/MSBuild.dll";
        start.Environment["MSBuildExtensionsPath"] = "/sdk/10/";
        start.Environment["MSBuildSDKsPath"] = "/sdk/10/Sdks";
        start.Environment["KEEP_ME"] = "1";

        Assert.Same(start, CompilerBase.WithoutLocatorEnvironment(start));

        Assert.False(start.Environment.ContainsKey("MSBUILD_EXE_PATH"));
        Assert.False(start.Environment.ContainsKey("MSBuildExtensionsPath"));
        Assert.False(start.Environment.ContainsKey("MSBuildSDKsPath"));
        Assert.Equal("1", start.Environment["KEEP_ME"]);
    }
}
