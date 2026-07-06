# ADR 0006: Play Mode Architecture

**Status**: Implemented

## Context
The editor needs Play/Pause/Step/Stop — running the game simulation in the viewport to test gameplay without leaving the editor. Must support script execution, input forwarding, and scene rendering in the existing viewport.

## Decision
- **In-process hot-compiled shared library** (`libturian_play.so`), NOT a subprocess. Subprocess can't render into the studio's GPU viewport.
- `PlayBuild.zig` generates `play_main.zig` + `build.zig` → `b.addLibrary(.{.linkage=.dynamic})`. Compiles engine + scripts only (drops editor/guid/serde/oap).
- **C ABI** for crossing the dynamic library boundary: `turianPlayStart`, `Update`, `Stop`, `NodesPtr`, `NodesCount`, `NewFrame`, `SetKey`, `SetMouseButton`, `SetMousePos`, `AddMouseMotion`, `AddWheel`, `LoadInputActions`.
- **Critical serde workaround**: serde's JSON parser GP faults inside the dynamic library (AVX memmove reads bad src pointer). Solution: don't serialize the scene across the boundary. Studio passes `&EditorState.objects` + count via C ABI; lib `@memcpy`s its own copy. Works because `SceneNode` is self-contained POD (fixed buffers, no heap ptrs) and studio+lib share identical engine layout. Play lib links NO serde/editor.

## Consequences
- Studio snapshots/restores `EditorState.objects` on Play/Stop.
- Per-frame `pump(io)` maps dvui input → engine input.
- Rebuild cache: only on source-hash change.
- Known gaps: no gamepad in Play mode, Inspector shows edit-time values not live game values, single-scene only.
- Out-of-process play for crash isolation deferred.
