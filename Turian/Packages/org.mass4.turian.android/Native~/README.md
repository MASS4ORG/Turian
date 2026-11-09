# Android native libraries

`arm64-v8a` and `x86_64` contain the SDL and Silk launcher libraries used by Android exports. `AndroidNative.targets` replaces the copies embedded in the Silk.NET.Windowing.Sdl AAR before APK packaging. The exported APK validator checks each native ELF load segment and the ZIP offset of uncompressed native entries.

Rebuild from the repository root with `dotnet run tools/android-native.cs -- <NDK directory> <CMake executable>`. The build requires NDK 29.0.14206865; CMake 3.31.6 is the qualified build version. Both native targets use `-Wl,-z,max-page-size=16384` and `-Wl,-z,common-page-size=16384`. `build.json` records source revisions and output hashes.

The SDL source is release 2.30.8 because the Java code bundled in Silk.NET.Windowing.Sdl 2.23.0 checks that exact native version. The Silk launcher source is pinned to the 2.23.0 release commit. Upstream license notices accompany the binaries.

The 16 KB acceptance emulator uses `system-images;android-36;google_apis_ps16k;x86_64`, a Pixel 7 profile, four virtual CPUs, 3072 MB RAM and a cold boot. Confirm `adb shell getconf PAGE_SIZE` returns `16384` and disable page-size compatibility mode before running acceptance. The private Android acceptance ledger owns the exact installed image/emulator revisions and execution results.
