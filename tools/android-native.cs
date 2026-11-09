#!/usr/bin/env dotnet
#:property GenerateDocumentationFile=true
// Rebuilds the SDL Android prebuilts used by the Android launcher.
// Usage: dotnet run tools/android-native.cs -- <NDK directory> <CMake executable>
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/android-native.cs -- <NDK directory> <CMake executable>");
    return 2;
}

var root = Directory.GetCurrentDirectory();
var work = Path.Combine(root, ".local", "android-native");
var output = Path.Combine(root, "Turian", "Packages", "org.mass4.turian.android", "Native~");
var ndk = Path.GetFullPath(args[0]);
var cmake = Path.GetFullPath(args[1]);
var host = OperatingSystem.IsWindows() ? "windows-x86_64"
    : OperatingSystem.IsMacOS() ? "darwin-x86_64" : "linux-x86_64";
var strip = Path.Combine(ndk, "toolchains", "llvm", "prebuilt", host, "bin",
    OperatingSystem.IsWindows() ? "llvm-strip.exe" : "llvm-strip");
var ninja = Path.Combine(Path.GetDirectoryName(cmake)!, OperatingSystem.IsWindows() ? "ninja.exe" : "ninja");
if (!File.ReadAllText(Path.Combine(ndk, "source.properties")).Contains("29.0.14206865", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The Android native build requires NDK 29.0.14206865.");
}
Directory.CreateDirectory(work);
using var client = new HttpClient();
const string sdlCommit = "79ec168f3c1e2fe27335cb8886439f7ef676fb49";
const string silkCommit = "94605142f7b7bd6e69c9201e8e721d245c69eb7e";
var archive = Path.Combine(work, "sdl.zip");
await Download($"https://codeload.github.com/libsdl-org/SDL/zip/{sdlCommit}", archive);
var sdl = Path.Combine(work, "SDL-" + sdlCommit);
if (!Directory.Exists(sdl))
{
    ZipFile.ExtractToDirectory(archive, work);
}
foreach (var name in new[] { "libsilkdroid.c", "libsilkdroid.h" })
{
    await Download($"https://raw.githubusercontent.com/dotnet/Silk.NET/{silkCommit}/src/Windowing/SilkDroid/app/jni/src/{name}",
        Path.Combine(work, name));
}
File.WriteAllText(Path.Combine(work, "CMakeLists.txt"), $$"""
    cmake_minimum_required(VERSION 3.22)
    project(TurianAndroidNative C)
    set(SDL_SHARED ON CACHE BOOL "" FORCE)
    set(SDL_STATIC OFF CACHE BOOL "" FORCE)
    set(SDL_TEST OFF CACHE BOOL "" FORCE)
    set(SDL_TESTS OFF CACHE BOOL "" FORCE)
    set(SDL_SOURCE_VERSION "2.30.8-g{{sdlCommit}}")
    add_subdirectory("{{sdl.Replace('\\', '/')}}" SDL)
    target_link_options(SDL2 PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
    add_library(main SHARED libsilkdroid.c)
    target_link_libraries(main PRIVATE SDL2 log GLESv1_CM GLESv2)
    target_link_options(main PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
    """);
var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var abi in new[] { "arm64-v8a", "x86_64" })
{
    var build = Path.Combine(work, "build-" + abi);
    Run(cmake, "-S", work, "-B", build, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja,
        "-DCMAKE_TOOLCHAIN_FILE=" + Path.Combine(ndk, "build", "cmake", "android.toolchain.cmake"),
        "-DANDROID_ABI=" + abi, "-DANDROID_PLATFORM=android-33", "-DCMAKE_BUILD_TYPE=Release");
    Run(cmake, "--build", build, "--parallel", "4", "--target", "SDL2", "main");
    var destination = Path.Combine(output, abi);
    Directory.CreateDirectory(destination);
    foreach (var name in new[] { "libSDL2.so", "libmain.so" })
    {
        var source = Directory.GetFiles(build, name, SearchOption.AllDirectories).Single();
        var target = Path.Combine(destination, name);
        File.Copy(source, target, overwrite: true);
        Run(strip, "--strip-debug", target);
        hashes[abi + "/" + name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
    }
}
File.Copy(Path.Combine(sdl, "LICENSE.txt"), Path.Combine(output, "SDL-LICENSE.txt"), overwrite: true);
await Download($"https://raw.githubusercontent.com/dotnet/Silk.NET/{silkCommit}/LICENSE.md",
    Path.Combine(output, "Silk-LICENSE.md"));
using (var stream = File.Create(Path.Combine(output, "build.json")))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("Sdl", sdlCommit);
    writer.WriteString("Silk", silkCommit);
    writer.WriteString("Ndk", "29.0.14206865");
    writer.WriteNumber("Api", 33);
    writer.WriteStartArray("LinkOptions");
    writer.WriteStringValue("-Wl,-z,max-page-size=16384");
    writer.WriteStringValue("-Wl,-z,common-page-size=16384");
    writer.WriteEndArray();
    writer.WriteStartObject("Sha256");
    foreach (var (name, hash) in hashes)
    {
        writer.WriteString(name, hash);
    }
    writer.WriteEndObject();
    writer.WriteEndObject();
}
return 0;

async Task Download(string url, string path)
{
    if (!File.Exists(path))
    {
        var data = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, data);
    }
}

static void Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start native build.");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Native build exited with code {process.ExitCode}.");
    }
}
