namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for packaging published output into release archives.
/// </summary>
sealed partial class Build
{
    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath PackedArchive =>
        ArtifactsDirectory / $"Turian-{Version}-{RuntimeIdentifier}.zip";

    // Not declared via .Produces(PackedArchive): that evaluates its argument eagerly while Nuke
    // builds the target graph, before GitVersion's [GitVersion] field injection has run, and
    // PackedArchive depends on Version (=> gitVersion.MajorMinorPatch), which would NRE.
    public Target Pack => td =>
        td
            .DependsOn(Publish, WindowsInstaller)
            .Executes(() =>
            {
                ArtifactsDirectory.CreateDirectory();
                PackedArchive.DeleteFile();
                ZipFile.CreateFromDirectory(PublishDir, PackedArchive, CompressionLevel.Optimal, includeBaseDirectory: false);
                Log.Information("Packed {PublishDir} into {PackedArchive}", PublishDir, PackedArchive);
            });
}
