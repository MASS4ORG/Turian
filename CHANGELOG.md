# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Changed: a project's brick folder is `Bricks/` instead of `Packages/` (manifest, lock file and local bricks); a project that still has `Packages/manifest.json` is renamed when it is opened, commit the rename
- Changed: camera rigs (Follow, Orbit, Free Fly, FPS) moved to the built-in brick `org.mass4.turian.cameras` (namespace `Turian.Cameras`); projects add it to `Bricks/manifest.json` as `builtin:org.mass4.turian.cameras`
- Changed: the in-game UI (`.ui`/`.uss`, Guinevere, Skia) moved to the built-in brick `org.mass4.turian.ui`; games without it no longer ship Guinevere or Skia. Projects using UI add `builtin:org.mass4.turian.ui` to `Bricks/manifest.json` and `using Turian.Engine.UI;` where they use it (new projects no longer get it as a global using)
- Fixed: a meta whose asset type is not installed is left untouched instead of being rewritten with a new id
- Added: `nuget` field in `package.json`, `Precast~/` prebuilt assemblies, and the `IUiPresenter` contract hosts draw interfaces through
- Added: `turian-cli import unitypackage`: textures, models and audio keep their Unity guids as asset ids, import settings map where they exist, materials convert to Turian materials, prefabs and scenes to prefabs (hierarchy, transforms, lights, cameras, mesh renderers); scripts, shaders and animation are reported and left out
- Added: brick registries (`docs/bricks/registry.md`): a static site with a signed index; projects scope registries to name prefixes in `Bricks/manifest.json` and trust them with their own keys, version ranges resolve from them and the lock pins version, hash and signing key; `turian-cli brick publish|yank|search|registry`; signatures are OpenSSH ed25519 signatures checked with a built-in verifier
- Added: `store` terms in `package.json` (token entitlement, license agreement, `redistribute`, `embeddable`), enforced by `embed` and `pack`
- Added: team flows for bricks: `turian-cli brick stub` makes a placeholder brick with the same asset and type ids, `brick verify --against` checks it (or any brick) still covers the real one, `brick diff` lists what an embedded fork changed and `brick rebase` merges a new release of its original into it three ways
- Added: Bricks panel (Project → Bricks…): installed bricks, details with what needs and requires each, install, update, remove, embed, restore, and copy an asset into the project under a new id
- Added: per-project import settings for brick assets in `ProjectSettings/PackageImportOverrides.json` (asset id → meta properties to replace), applied on import without touching the brick
- Added: data asset variants (`__Variant`: base asset id + overrides), resolved wherever a data asset is read; `turian-cli variant` creates one, `turian-cli brick copy` copies brick assets into the project
- Added: `turian-cli brick new|add|remove|list|restore|update|embed|verify|pack` and the `.brick` transport file; `pack --precast` compiles a brick's assemblies into `Precast~` so consumers load them instead of compiling the code
- Changed: the brick store is `~/.gaya/bricks` (override with `GAYA_BRICKS`); studio-scope bricks ship their plugin assemblies in `Precast~/lib`
- Added: reusable GitHub workflow `.github/workflows/brick.yml` to verify, pack and release a brick and restore a game's bricks from its lock file
- Added: `builtin:` package source; new projects install the default built-in bricks
- Fixed: closing the window with no unsaved work exited through a second close that a veto then undid, freezing the editor
- Changed: code sytles fixes

## [1.2.0] - 2026-09-29

- Added: inspector property drawers and metadata #28 #29
- Fixed: Rider/Qodana warnings

## [1.1.0] - 2026-09-28

- Added: undo/redo with per-document history #81
- Added: nested prefabs, variants, override tracking, and themed controls #84 #185
- Fixed: start the asset importer when a project opens
- Changed: split CRAP hotspots and cover them with tests

## [1.0.1] - 2026-09-25

- Fixed: publish successful platform artifacts even when one build fails

## [1.0.0] - 2026-09-25

- First commit!

[Unreleased]: https://github.com/MASS4ORG/Turian/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/MASS4ORG/Turian/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/MASS4ORG/Turian/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/MASS4ORG/Turian/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/MASS4ORG/Turian/releases/tag/v1.0.0
