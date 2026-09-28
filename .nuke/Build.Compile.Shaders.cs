namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for the compiling shaders.
/// </summary>
sealed partial class Build
{
    [Parameter("skip-shaders (default: false)")]
    readonly bool SkipShaders;

    [Parameter("spirv-version (default: spv1.6)")]
    readonly string SpirVVersion = "spv1.6";
    readonly AbsolutePath EngineProjectDirectory = RootDirectory / "Turian" / "Engine";

    // Compiling shaders
    static readonly string[] ShaderPatterns =
    [
        "**/*.vert",
        "**/*.frag",
        "**/*.comp",
        "**/*.task",
        "**/*.mesh"
    ];

    Target CompileShaders => td =>
        td
            .OnlyWhenStatic(() => !SkipShaders)
            .Executes(() =>
            {
                // Deleting existing .spv files
                var existingSpvFiles = EngineProjectDirectory.GlobFiles("**/*.spv");
                foreach (var file in existingSpvFiles)
                {
                    file.DeleteFile();
                }
                var compiled = 0;
                foreach (var pattern in ShaderPatterns)
                {
                    var shaders = EngineProjectDirectory.GlobFiles(pattern);

                    foreach (var shader in shaders)
                    {
                        compiled++;
                        var output = $"{shader}.spv";
                        var process = ProcessTasks.StartProcess(
                            "glslc",
                            $"\"{shader}\" -o \"{output}\" --target-spv={SpirVVersion}"
                        );
                        _ = process.AssertZeroExitCode();
                    }
                }

                if (compiled == 0)
                {
                    throw new InvalidOperationException(
                        $"No shader sources found under {EngineProjectDirectory}.");
                }
            });
}
