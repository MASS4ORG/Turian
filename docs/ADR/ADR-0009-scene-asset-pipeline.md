# ADR 0009: Scene & Asset Pipeline

**Status**: Implemented

## Context
The engine needs a scene representation (entity hierarchy with components), serialization, runtime loading, and an asset pipeline that ties authoring metadata to imported assets. Issues #22 (Scene Management) and #43 (TypedAssetRef).

## Decision
- **SceneNode as self-contained POD**: fixed buffers, no heap pointers. Passable across C ABI boundary. No monolithic `Scene` type — SceneNode array is the scene.
- **SceneManager** (`engine/scene/SceneManager.zig`): allocator-based, decoupled from parsing via injected `Loader` fn. Generational `SceneHandle{index, generation}` for stale-handle detection. Supports `.single` / `.additive` loading, async with `isReady`/`loadProgress`, deferred requests (`requestLoad`/`requestUnload` → `flushRequests()`).
- **MetaFile** (`editor/types/MetaFile.zig`): asset metadata stored alongside source files (`.meta`). Contains GUID, `source_hash` (for reimport detection), SubAsset array, import settings. Never store full filesystem paths — use project-relative paths.
- **TypedAssetRef(.scene)** (`engine/api/AssetRef.zig`): `AssetFilter` enum gained `.scene`; `guid()` accessor. Runtime hydration: `GameBuild.hydrateComponent` detects `_turian_ref_kind` on fields → sets from `as_ref_guid`.
- **Serialization: JSON** (serde.json) for all authored assets. Not ZON (project convention).

## Consequences
- SceneNodes are trivially copyable — enables Play Mode's C ABI pass-through.
- MetaFile `source_hash` drives automatic reimport — no manual refresh.
- `hydrateComponent` uses field name convention to detect AssetRef fields — no annotation needed.
- Async streaming engine API ready; runtime boots sync. Per-object DontDestroyOnLoad deferred.
