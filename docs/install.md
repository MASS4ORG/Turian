# Engine Developer Setup

This guide is for contributors and developers who want to build Turian from source.

---

## Requirements

| Tool | Minimum version | Notes |
|------|----------------|-------|
| [Zig](https://ziglang.org/download/) | **0.16.0** | Must match exactly; 0.15 and below are not supported |
| Git | any | For cloning and LFS |
| Git LFS | any | Required for binary assets in `examples/` |

Zig 0.16.0 is self-contained — no C compiler or system SDK is needed on
Windows.  On Linux/macOS the system `libc` must be present.

---

## Clone the repository

```bash
git lfs install            # once per machine, to enable LFS
git clone <repo-url> turian
cd turian
```

---

## Build

```bash
zig build          # compile studio + CLI (Debug)
zig build run      # compile and launch the editor
zig build test     # run engine + editor unit tests
zig build ci       # tests + ReleaseFast artifacts (used in CI)
```

Output binaries are in `zig-out/bin/`:
- `turian-studio` — the GUI editor
- `turian-cli`    — the headless CLI

---

## CLI usage

```bash
# Create a new project
./zig-out/bin/turian-cli new-project ../my-game "My Game"

# Print project metadata
./zig-out/bin/turian-cli info ../my-game

# Build the game executable
./zig-out/bin/turian-cli build ../my-game
```

The CLI uses build-time paths by default.  Override with env vars when running
a pre-built binary on another machine:

```bash
export TURIAN_ENGINE_ROOT=/path/to/turian/engine/root.zig
export TURIAN_EDITOR_ROOT=/path/to/turian/editor/root.zig
export TURIAN_BUILD_ROOT=/path/to/turian
./turian-cli build ../my-game
```

---

## Adding a built-in component

1. Create `engine/components/MyComponent.zig`:
   ```zig
   pub const MyComponent = struct {
       value: f32 = 1.0,
   };
   ```
2. Add it to `engine/components/root.zig` (follow the existing pattern).
3. Add a tag to the `Component` union in `engine/scene/Component.zig`.
4. Update `BuiltinEntry.zig` and `scanner.zig` (`populateBuiltins`).

---

## Running the tests

```bash
zig build test
```

Tests live inline in source files using Zig's built-in `test` blocks.

---

## Platform notes

| Platform | Studio | CLI | 3D viewport |
|----------|--------|-----|-------------|
| Windows  | ✓      | ✓   | Vulkan driver only (D3D12 falls back to a disabled viewport) |
| Linux    | ✓      | ✓   | ✓ (Vulkan) |
| macOS    | ✓      | ✓   | Metal falls back to a disabled viewport (SPIRV-only renderer) |

### GPU backend selection

The Studio uses SDL3-GPU. On startup it **prefers a Vulkan device** so the 3D
viewport (whose shaders are SPIRV) works, and **falls back** to the platform
default (D3D12 on Windows, Metal on macOS) when no Vulkan driver is present. The
editor UI renders on any of these backends — only the 3D scene viewport is
Vulkan-only and shows *"3D viewport unavailable"* on the fallback path. Most
modern Windows GPUs ship a Vulkan driver, so the viewport works out of the box;
a bare install with no Vulkan driver still gets a fully functional editor minus
the 3D preview.

### Cross-compiling for Windows from Linux

Zig cross-compiles the whole Studio (SDL3, dvui, freetype and all are built from
source) — no Windows toolchain required:

```bash
zig build -Dtarget=x86_64-windows-gnu -Dno-test
```

`-Dno-test` is required because the Linux host can't run the Windows test
binaries. The resulting `zig-out/bin/turian-studio.exe` is self-contained (SDL3
is statically linked; it only imports standard system DLLs) and runs under Wine
for smoke-testing.
