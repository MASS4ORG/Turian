# ADR 0007: Gizmo System

**Status**: Implemented

## Context
The editor needs 3D manipulators (translate/rotate/scale gizmos), selection highlights, and debug visualization. These must work in both the editor viewport and potentially from user scripts. Key constraint: gizmo code must not pull in GPU or GUI dependencies.

## Decision
- **Pure-data immediate-mode buffer** (`engine/Gizmos.zig`). Produces lists of lines, points, and labels with world-space positions. No GPU types, no GUI types — pure `engine` module.
- Usable from user scripts (same API as editor gizmos).
- **Rendering** delegated to `subsystems/render/gizmos.zig`: depth-tested (world) + overlay (no depth) pipelines. Per-vertex line thickness via camera-facing quads (TRIANGLELIST expanded from pairs).
- **Interaction** (`studio/GizmoSystem.zig`): interactive transform gizmo (W/E/R), screen-space axis picking, snapping, undo per drag. Click-to-select ray-cast on non-handle press.

## Consequences
- Engine/editor separation: gizmo data format is stable regardless of renderer changes.
- Lines support thickness without GPU line-width extension.
- Shaders compiled manually via `glslc` (not built by `zig build`).
