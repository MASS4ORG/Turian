namespace Build;

/// <summary>
/// Generates manifests for submission to the Winget community repository.
/// See .nuke/packaging/windows/README.md before publishing them.
/// </summary>
sealed partial class Build
{
    [Parameter("Public HTTPS URL of the Windows installer, used in generated Winget manifests")]
    readonly string WingetInstallerUrl;

    [Parameter("Generate platform package manifests with a placeholder URL and without publishing")]
    readonly bool PackagingDryRun;

    public Target WingetManifest => td => td
        .DependsOn(WindowsInstaller)
        .Executes(() =>
        {
            var installerUrl = WingetInstallerUrl;
            if (string.IsNullOrWhiteSpace(installerUrl))
            {
                if (!PackagingDryRun)
                {
                    throw new InvalidOperationException("WingetManifest requires --winget-installer-url outside a dry run.");
                }

                installerUrl = $"https://example.invalid/Turian-{Version}-win-x64-setup.exe";
            }

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
                    InstallerUrl: {installerUrl}
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
                LicenseUrl: https://github.com/MASS4ORG/Turian/blob/main/docs/LICENSE.md
                ManifestType: defaultLocale
                ManifestVersion: 1.12.0
                """ + "\n");
        });

    /// <summary>Validates the generated manifests with `winget validate`, which only exists on Windows.</summary>
    public Target WingetValidate => td => td
        .DependsOn(WingetManifest)
        .OnlyWhenStatic(() => RuntimeIdentifier == "win-x64")
        .Executes(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("WingetValidate needs Windows: winget has no Linux or macOS client.");
            }

            var winget = FindWinget() ?? (IsServerBuild ? InstallWinget() : null)
                ?? throw new InvalidOperationException("winget was not found: install App Installer from the Microsoft Store.");
            ProcessTasks.StartProcess(winget,
                    $"validate --manifest \"{ArtifactsDirectory / "winget" / Version}\" --disable-interactivity")
                .AssertZeroExitCode();
        });

    /// <summary>Finds winget on the PATH or at its per-user App Installer alias, or returns null.</summary>
    static string FindWinget() =>
        FindOnPath("winget") ?? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "winget.exe")
            }
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Installs winget through the Microsoft.WinGet.Client module, because CI Windows images may not ship it.
    /// Only CI does this; a developer machine gets winget from the Microsoft Store instead.
    /// </summary>
    static string InstallWinget()
    {
        var shell = FindOnPath("pwsh") ?? "powershell";
        ProcessTasks.StartProcess(shell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                "Install-PackageProvider -Name NuGet -Force | Out-Null; " +
                "Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery | Out-Null; " +
                "Repair-WinGetPackageManager -AllUsers -Latest -Force\"")
            .AssertZeroExitCode();
        return FindWinget();
    }
}
