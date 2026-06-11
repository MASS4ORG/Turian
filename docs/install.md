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

| Platform | Studio | CLI | User reflection |
|----------|--------|-----|-----------------|
| Windows  | ✓      | ✓   | not yet (dlopen) |
| Linux    | ✓      | ✓   | ✓               |
| macOS    | ✓      | ✓   | ✓               |
