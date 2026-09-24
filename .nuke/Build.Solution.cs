namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the solution-wide variables.
/// </summary>
sealed partial class Build
{
    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string configuration;

    string Configuration =>
        configuration ?? (IsLocalBuild ? ConfigurationOptions.Debug : ConfigurationOptions.Release);

    [Solution(GenerateProjects = true)]
    private readonly Solution Solution;
}
