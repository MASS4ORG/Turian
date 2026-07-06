# ADR 0003: Asset Preview API

**Status**: Implemented (MVP)

## Context
Editor needs Unity-style `PreviewRenderUtility` — inline thumbnails in Asset Browser + interactive preview panels in Inspector. Must be shared, cached, and not stall the editor.

## Decision
- **Provider registry**: `PreviewSystem.zig` maps `AssetType` → `ProviderFn`. Built-in providers for texture, model, material, audio. Extension point for editor plugins.
- **Two-tier cache**: in-memory FIFO `[256]CacheEntry` (owned RGBA8) + on-disk `<project>/.cache/thumbnails/<guid>.thumb` (24-byte header + raw RGBA8, no codec dep).
- **Cache key**: GUID + MetaFile `source_hash`. Reimport invalidates automatically. Failed providers cached as empty-pixel sentinel.
- **Frame budget**: 3 cache-miss generations per frame to avoid stalls.
- **GPU path**: `renderPreview` (reuses shared cmd buffer) vs `renderAndCapture` (one-shot with fence, correct from first call). Both save/restore editor camera.
- **PreviewCamera.Orbit**: `{target, yaw, pitch, distance, fov}` with auto-fit `frame()`.

## Consequences
- No codec dependency for thumbnail storage — raw RGBA8 is fast and simple.
- Material editor live preview via `setMaterialOverride(guid, bytes)` / `clearMaterialOverride()` — no disk writes during edit.
- Audio limited to PCM16 `.wav` (no ogg/mp3 decoder). Compressed textures (KTX2/BCn) skip thumbnail.
