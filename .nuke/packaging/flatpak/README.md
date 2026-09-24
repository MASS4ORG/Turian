# Flatpak

Build a local Studio bundle with `./build.sh Flatpak --configuration Release`.
The target uses the .NET 10 Flatpak SDK extension and includes the .NET SDK because Studio compiles user projects.
It grants GPU access, Wayland/X11 access, networking, and home-directory project access. It is intentionally not in CI
until the NuGet source lock and Flathub submission policy are finalized.
