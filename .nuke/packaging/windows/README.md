# Windows packaging

The installer uses NSIS because `makensis` can create a Windows installer on Linux, macOS, or Windows.
The installer is per-user, needs no administrator rights, installs both Studio and CLI, adds the install folder to
the user `PATH` (so `turian-studio` and `turian-cli` work from any terminal) and a Start menu shortcut for Studio.
It warns, without blocking, when the .NET 10 SDK is missing. Uninstalling removes the `PATH` entry.

1. Install .NET 10 SDK and NSIS. On Debian/Ubuntu, run `sudo apt install nsis`.
2. Build the installer from any host that can publish `win-x64`:

   ```sh
   ./build.sh WindowsInstaller \
     --configuration Release \
     --runtime-identifier win-x64 \
     --publish-self-contained false \
     --publish-single-file false
   ```

3. Test the generated `artifacts/Turian-<version>-win-x64-setup.exe` on Windows. A framework-dependent
   build requires the .NET 10 runtime/SDK on the target computer. Code-sign the final installer before release.
4. Upload that exact, signed installer to a stable HTTPS release URL. Signing or modifying it changes its hash.
5. Generate Winget manifests using the final URL:

   ```sh
   ./build.sh WingetManifest \
     --configuration Release \
     --runtime-identifier win-x64 \
     --winget-installer-url https://example.org/releases/Turian-<version>-win-x64-setup.exe
   ```

6. Validate `artifacts/winget/<version>` with `wingetcreate validate` or the Windows Package Manager validation
   pipeline. Fork `microsoft/winget-pkgs`, place the three files under the matching
   `manifests/a/Turian/Turian/<version>/` directory, and open a pull request.

`publish.yml` builds the installer on every release (`Pack` depends on `WindowsInstaller`) but does not sign it, so
Windows SmartScreen warns on first run until signing is added. `WingetManifest` is not called by CI; run it once the
permanent download URL and the final Winget package identifier are confirmed.
