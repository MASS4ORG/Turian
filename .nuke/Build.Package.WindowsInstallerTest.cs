using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;

namespace Build;

/// <summary>
/// Installs, uses and uninstalls the Windows installer silently, checking its files, uninstall entry and user PATH.
/// It changes the current user's PATH, Start menu and uninstall entries, so it refuses to run over a real installation.
/// </summary>
sealed partial class Build
{
    static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(2);

    string UninstallKey => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductName}";

    AbsolutePath StartMenuDirectory =>
        (AbsolutePath)Environment.GetFolderPath(Environment.SpecialFolder.Programs) / ProductName;

    /// <summary>Runs the built installer on Windows, uses the installed product and uninstalls it.</summary>
    public Target WindowsInstallerTest => td => td
        .DependsOn(WindowsInstaller)
        .OnlyWhenStatic(() => RuntimeIdentifier == "win-x64")
        .Executes(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("WindowsInstallerTest runs the installer, so it needs Windows.");
            }

            TestWindowsInstaller();
        });

    /// <summary>Checks the installed product works, for example by running its executable from the install folder.</summary>
    partial void TestInstalledProduct(AbsolutePath installDirectory);

    [SupportedOSPlatform("windows")]
    void TestWindowsInstaller()
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            Check(existing is null, $"Uninstall {ProductName} first: WindowsInstallerTest would overwrite that installation.");
        }

        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        var originalPath = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var originalKind = originalPath is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");

        // Longer than the 1024-character NSIS string limit, proving the installer neither drops nor truncates it.
        var longPath = string.Join(';', Enumerable.Range(1, 40).Select(static index => $@"C:\{ProductName}-test\entry-{index}"));
        var installDirectory = (AbsolutePath)Path.GetTempPath() / $"{ProductName}-installer-test";
        installDirectory.DeleteDirectory();
        try
        {
            environment.SetValue("Path", longPath, RegistryValueKind.ExpandString);
            // NSIS reads /D= verbatim up to the end of the command line, so the folder is last and unquoted.
            ProcessTasks.StartProcess(WindowsInstallerFile, $"/S /D={installDirectory}").AssertZeroExitCode();

            Check((installDirectory / "Uninstall.exe").FileExists(), "The installer did not create Uninstall.exe.");
            Check(ReadUserPath(environment) == $"{longPath};{installDirectory}",
                "The installer did not append its folder to the existing user PATH.");
            using (var entry = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                Check(entry is not null, "The installer did not register its uninstall entry.");
            }

            Check(StartMenuDirectory.GlobFiles("*.lnk").Any(), "The installer did not create the Start menu shortcuts.");

            TestInstalledProduct(installDirectory);

            // The uninstaller copies itself to a temporary folder and returns at once, so wait for it to finish.
            ProcessTasks.StartProcess(installDirectory / "Uninstall.exe", "/S").AssertZeroExitCode();
            var watch = Stopwatch.StartNew();
            while (installDirectory.DirectoryExists() && watch.Elapsed < UninstallTimeout)
            {
                Thread.Sleep(500);
            }

            Check(!installDirectory.DirectoryExists(), "The uninstaller did not remove the install folder.");
            Check(ReadUserPath(environment) == longPath, "The uninstaller did not restore the other user PATH entries.");
            using (var entry = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                Check(entry is null, "The uninstaller did not remove its uninstall entry.");
            }

            Check(!StartMenuDirectory.DirectoryExists(), "The uninstaller did not remove the Start menu shortcuts.");
        }
        finally
        {
            if (originalPath is null)
            {
                environment.DeleteValue("Path", throwOnMissingValue: false);
            }
            else
            {
                environment.SetValue("Path", originalPath, originalKind);
            }

            // After a failed check, uninstall in place (_?=, synchronous) so the Start menu and uninstall entries go too.
            var uninstaller = installDirectory / "Uninstall.exe";
            if (uninstaller.FileExists())
            {
                ProcessTasks.StartProcess(uninstaller, $"/S _?={installDirectory}").WaitForExit();
            }

            installDirectory.DeleteDirectory();
        }
    }

    [SupportedOSPlatform("windows")]
    static string ReadUserPath(RegistryKey environment) =>
        environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }
}
