namespace Turian.NUKE;

/// <summary>
/// Generates manifests for submission to the Winget community repository.
/// See .nuke/packaging/windows/README.md before publishing them.
/// </summary>
sealed partial class Build
{
    [Parameter("Public HTTPS URL of the Windows installer, used in generated Winget manifests")]
    readonly string wingetInstallerUrl;

    public Target WingetManifest => td => td
        .DependsOn(WindowsInstaller)
        .Requires(() => wingetInstallerUrl)
        .Executes(() =>
        {
            using var stream = File.OpenRead(WindowsInstallerFile);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            var manifestDirectory = ArtifactsDirectory / "winget" / Version;
            manifestDirectory.CreateOrCleanDirectory();

            (manifestDirectory / "MASS4.Turian.yaml").WriteAllText($"""
                # yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
                PackageIdentifier: MASS4.Turian
                PackageVersion: {Version}
                DefaultLocale: en-US
                ManifestType: version
                ManifestVersion: 1.12.0
                """ + "\n");
            (manifestDirectory / "MASS4.Turian.installer.yaml").WriteAllText($"""
                # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
                PackageIdentifier: MASS4.Turian
                PackageVersion: {Version}
                InstallerType: nullsoft
                Scope: user
                Dependencies:
                  PackageDependencies:
                    - PackageIdentifier: Microsoft.DotNet.SDK.10
                Installers:
                  - Architecture: x64
                    InstallerUrl: {wingetInstallerUrl}
                    InstallerSha256: {hash}
                ManifestType: installer
                ManifestVersion: 1.12.0
                """ + "\n");
            (manifestDirectory / "MASS4.Turian.locale.en-US.yaml").WriteAllText($"""
                # yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
                PackageIdentifier: MASS4.Turian
                PackageVersion: {Version}
                PackageLocale: en-US
                Publisher: MASS4
                PackageName: Turian
                License: MPL-2.0
                ShortDescription: .NET game engine editor and command-line tools
                PackageUrl: https://github.com/MASS4ORG/Turian
                LicenseUrl: https://github.com/MASS4ORG/Turian/blob/main/LICENSE.md
                ManifestType: defaultLocale
                ManifestVersion: 1.12.0
                """ + "\n");
        });
}
