# Turian Engine SDK

The Turian SDK is a **portable, self-contained** bundle that lets you build games
without the engine source checkout. It includes the engine/editor Zig sources, all
required dependencies, the CLI tool, and (on desktop platforms) the studio and the
SDL3 library.

## Prerequisite

**Zig 0.16.0** must be on your `PATH`. No other toolchain is required — the CLI
shells out to `zig build` to compile your game.

Download Zig from <https://ziglang.org/download/>.

> **Why Zig is not bundled:** Zig has no stable ABI, so the engine cannot be shipped
> as a precompiled library — your game is compiled *together* with the engine source.
> The SDK ships that source; you supply the compiler. (The one exception is SDL3,
> a C library, which *is* shipped precompiled in `lib/`.)

## Install (portable — no installation step)

Extract the archive anywhere. Nothing is written to your system; the bundle is fully
relocatable.

```sh
# Linux / macOS
tar xf turian-sdk-linux-x86_64-v1.0.0.tar.gz
export PATH="$PWD/turian-sdk-linux-x86_64-v1.0.0:$PATH"

# Windows (PowerShell)
Expand-Archive turian-sdk-windows-x86_64-v1.0.0.zip
$env:Path += ";$PWD\turian-sdk-windows-x86_64-v1.0.0"
```

The executables sit at the **root** of the bundle, so they are immediately visible.

## Create and build a game

```sh
turian-cli new-project mygame      # scaffold a project
turian-cli build mygame            # compile it into a standalone game
mygame/.cache/zig-out/bin/game     # run it
```

`turian-cli build`:
1. Scans `mygame/assets/` for user scripts (`@component` files).
2. Cooks all assets into `mygame/.cache/game.oap`.
3. Generates `build.zig` + `main.zig` in `mygame/.cache/` and compiles them with Zig.
4. Produces `mygame/.cache/zig-out/bin/game[.exe]` alongside `game.oap`.

The resulting game executable is self-contained: it reads every asset from `game.oap`
in its own directory and needs no engine source tree.

## Multiple SDK versions

Yes — each release extracts to its own `turian-sdk-<platform>-v<version>/` folder, and
they can coexist freely. The CLI locates the engine sources via the `turian-sdk.json`
marker **next to its own binary**, so each `turian-cli` always uses its own sibling
sources. To pick a version, put its folder on `PATH` or invoke it by full path:

```sh
~/sdks/turian-sdk-linux-x86_64-v1.0.0/turian-cli build mygame   # build with 1.0.0
~/sdks/turian-sdk-linux-x86_64-v1.1.0/turian-cli build mygame   # build with 1.1.0
```

No shared/global state is involved.

## SDK layout

```
turian-sdk-<platform>-v<version>/
  turian-cli[.exe]         headless build tool
  turian-studio[.exe]      GUI editor (desktop platforms only)
  turian-sdk.json          SDK marker (used by the CLI for path resolution)
  engine/                  engine Zig source + vendored C (cgltf, stb)
  editor/                  editor Zig source
  deps/
    math3d/src/            math library
    guid/src/              UUID library
    serde/src/             serialization library
    open_asset_package/src/  .oap reader/writer
  lib/
    libSDL3.a              SDL3 static library (desktop platforms only)
```

## Platform notes

| Platform        | Studio | SDL3 | Build windowed games |
|-----------------|:------:|:----:|:--------------------:|
| linux-x86_64    |  yes   | yes  | yes                  |
| windows-x86_64  |  yes   | yes  | yes                  |
| macos-aarch64   |  no    | no   | not yet (CLI/tooling only) |

macOS SDKs are currently CLI-only (cross-compiled, without SDL3), so building a
windowed game on macOS is not yet supported.

## Environment variable overrides

For advanced use (e.g. testing a patched engine alongside a released SDK), individual
paths can be overridden. Env vars take priority over SDK-relative paths, which take
priority over build-time baked paths (only present in in-tree dev builds).

| Variable                | Overrides                              |
|-------------------------|----------------------------------------|
| `TURIAN_ENGINE_ROOT`    | `engine/root.zig`                      |
| `TURIAN_EDITOR_ROOT`    | `editor/root.zig`                      |
| `TURIAN_BUILD_ROOT`     | SDK root directory                     |
| `TURIAN_MATH3D_ROOT`    | `deps/math3d/src/root.zig`             |
| `TURIAN_GUID_ROOT`      | `deps/guid/src/root.zig`               |
| `TURIAN_OAP_ROOT`       | `deps/open_asset_package/src/root.zig` |
| `TURIAN_SERDE_ROOT`     | `deps/serde/src/root.zig`              |
| `TURIAN_SDL3_LIB`       | `lib/libSDL3.a`                        |
| `TURIAN_CGLTF_WRAP_C`   | `engine/vendor/cgltf_wrap.c`           |
| `TURIAN_VENDOR_INCLUDE` | `engine/vendor/`                       |
