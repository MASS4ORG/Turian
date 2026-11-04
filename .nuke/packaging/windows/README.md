# Windows packaging

The installer uses NSIS. `makensis` can create it on Linux, macOS or Windows, but only Windows can run it, so CI builds and tests every Windows artifact on Windows runners.
The installer is per-user, needs no administrator rights, installs both Studio and CLI, adds the install folder to
the user `PATH` (so `turian-studio` and `turian-cli` work from any terminal) and a Start menu shortcut for Studio.
It warns, without blocking, when the .NET 10 SDK is missing. Uninstalling removes the `PATH` entry. [`user-path.ps1`](user-path.ps1) edits the `PATH`, because NSIS strings are capped at 1024 characters and would truncate a longer one.

1. Install .NET 10 SDK and NSIS (`choco install nsis` on Windows, `sudo apt install nsis` on Debian/Ubuntu). `WindowsInstaller` finds `makensis` on the `PATH` or in the default NSIS folder.
2. Build the installer and, on Windows, install, use and uninstall it:

   ```sh
   ./build.ps1 WindowsInstallerTest \
     --configuration Release \
     --runtime-identifier win-x64 \
     --publish-self-contained false \
     --publish-single-file false
   ```

   `WindowsInstallerTest` installs into a temporary folder with a user `PATH` longer than 1024 characters, runs the installed `turian-cli`, uninstalls it and restores the original `PATH`. It refuses to run while Turian is installed, because it shares the Start menu and uninstall entries. Use `WindowsInstaller` alone to only build the installer, on any host. A framework-dependent build requires the .NET 10 runtime/SDK on the target computer.
3. Code-sign the final installer before release.
4. Upload that exact, signed installer to a stable HTTPS release URL. Signing or modifying it changes its hash.
5. Generate Winget manifests using the final URL, and validate them on Windows with `WingetValidate`:

   ```sh
   ./build.ps1 WingetManifest WingetValidate \
     --configuration Release \
     --runtime-identifier win-x64 \
     --winget-installer-url https://example.org/releases/Turian-<version>-win-x64-setup.exe
   ```

6. Fork `microsoft/winget-pkgs`, place the three files from `artifacts/winget/<version>` under the matching
   `manifests/a/Turian/Turian/<version>/` directory, and open a pull request.

`publish-on-tag.yml` and the GitLab `build-windows` job build and test the installer on every release (`Pack` depends on `WindowsInstaller`) but do not sign it, so Windows SmartScreen warns on first run until signing is added. The `windows` job of `build-and-test.yml` and the GitLab `package-check-windows` job run the same test on every commit, plus `WingetManifest WingetValidate` with `--packaging-dry-run`; on CI, `WingetValidate` installs winget through the `Microsoft.WinGet.Client` PowerShell module when the image lacks it. Run `WingetManifest` with the real URL once the permanent download URL and the final Winget package identifier are confirmed.
