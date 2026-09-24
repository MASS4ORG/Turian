namespace Turian.NUKE;

/// <summary>
/// Produces independently installable Debian packages for the shared runtime, CLI, and Studio.
/// </summary>
sealed partial class Build
{
    static readonly IReadOnlyDictionary<string, string> debianArchitectures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "amd64",
            ["linux-arm64"] = "arm64"
        };

    string DebianArchitecture => debianArchitectures[runtimeIdentifier];

    public Target DebianPackage => td => td
        .DependsOn(Publish)
        .OnlyWhenStatic(() => debianArchitectures.ContainsKey(runtimeIdentifier))
        .Executes(() =>
        {
            var stagingRoot = (AbsolutePath)Path.Combine(Path.GetTempPath(), $"turian-debian-{runtimeIdentifier}");
            stagingRoot.DeleteDirectory();
            BuildCommonPackage(stagingRoot / "common");
            BuildApplicationPackage(stagingRoot / "cli", "turian-cli", "Turian command-line tools");
            AddStudioDesktopEntry(stagingRoot / "studio");
            BuildApplicationPackage(stagingRoot / "studio", "turian-studio", "Turian graphical editor");
        });

    void BuildCommonPackage(AbsolutePath packageRoot)
    {
        var applicationDirectory = packageRoot / "usr" / "lib" / "turian";
        applicationDirectory.CreateDirectory();
        ProcessTasks.StartProcess("cp", $"-a \"{PublishDirectory / "lib"}\" \"{applicationDirectory / "lib"}\"")
            .AssertZeroExitCode();
        WriteDebianMetadata(packageRoot, "turian-common", "dotnet-runtime-10.0, libfontconfig1, libvulkan1",
            "Turian shared libraries", "Shared managed and native libraries used by Turian tools.");
        BuildDebianArchive(packageRoot, "turian-common");
    }

    // The package installs the bootstrap launcher beside lib/, the layout CsProjectGenerator recognises as an
    // installed distribution, and puts a wrapper on the PATH.
    void BuildApplicationPackage(AbsolutePath packageRoot, string packageName, string summary)
    {
        var applicationDirectory = packageRoot / "usr" / "lib" / "turian";
        applicationDirectory.CreateDirectory();
        (PublishDirectory / packageName).Copy(applicationDirectory / packageName, ExistsPolicy.FileOverwrite);
        var binaryDirectory = packageRoot / "usr" / "bin";
        binaryDirectory.CreateDirectory();
        var launcher = binaryDirectory / packageName;
        launcher.WriteAllText($"""
            #!/bin/sh
            exec /usr/lib/turian/{packageName} "$@"
            """ + "\n");
        ProcessTasks.StartProcess("chmod", $"0755 \"{launcher}\" \"{applicationDirectory / packageName}\"")
            .AssertZeroExitCode();
        WriteDebianMetadata(packageRoot, packageName, $"turian-common (= {Version}), dotnet-sdk-10.0",
            summary, "Turian is a .NET game engine and editor built on Gaya.");
        BuildDebianArchive(packageRoot, packageName);
    }

    void AddStudioDesktopEntry(AbsolutePath packageRoot)
    {
        var flatpakDirectory = Solution.Build.Directory / "packaging" / "flatpak";
        (flatpakDirectory / "org.MASS4.Turian.desktop").Copy(
            packageRoot / "usr" / "share" / "applications" / "org.MASS4.Turian.desktop", ExistsPolicy.FileOverwrite);
        (flatpakDirectory / "org.MASS4.Turian.svg").Copy(
            packageRoot / "usr" / "share" / "icons" / "hicolor" / "scalable" / "apps" / "org.MASS4.Turian.svg",
            ExistsPolicy.FileOverwrite);
    }

    void WriteDebianMetadata(AbsolutePath packageRoot, string packageName, string dependencies, string summary,
        string details)
    {
        var documentationDirectory = packageRoot / "usr" / "share" / "doc" / packageName;
        documentationDirectory.CreateDirectory();
        (RootDirectory / "LICENSE.md").Copy(documentationDirectory / "copyright", ExistsPolicy.FileOverwrite);
        (packageRoot / "DEBIAN").CreateDirectory();
        (packageRoot / "DEBIAN" / "control").WriteAllText($"""
            Package: {packageName}
            Version: {Version}
            Architecture: {DebianArchitecture}
            Section: devel
            Priority: optional
            Maintainer: Bruno Massa <massa+turian@brunomassa.com>
            Depends: {dependencies}
            Homepage: https://github.com/MASS4ORG/Turian
            Description: {summary}
             {details}
            """ + "\n");
    }

    void BuildDebianArchive(AbsolutePath packageRoot, string packageName)
    {
        ArtifactsDirectory.CreateDirectory();
        var packageFile = ArtifactsDirectory / $"{packageName}_{Version}_{DebianArchitecture}.deb";
        packageFile.DeleteFile();
        ProcessTasks.StartProcess("dpkg-deb", $"--build --root-owner-group \"{packageRoot}\" \"{packageFile}\"")
            .AssertZeroExitCode();
    }
}
