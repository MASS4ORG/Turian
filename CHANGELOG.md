# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Changed: camera rigs (Follow, Orbit, Free Fly, FPS) moved to the built-in brick `org.mass4.turian.cameras` (namespace `Turian.Cameras`); projects add it to `Packages/manifest.json` as `builtin:org.mass4.turian.cameras`
- Changed: the in-game UI (`.ui`/`.uss`, Guinevere, Skia) moved to the built-in brick `org.mass4.turian.ui`; games without it no longer ship Guinevere or Skia. Projects using UI add `builtin:org.mass4.turian.ui` to `Packages/manifest.json` and `using Turian.Engine.UI;` where they use it (new projects no longer get it as a global using)
- Removed: `turian-cli ui` (use `screenshot` or `playmode` to render a document)
- Fixed: a meta whose asset type is not installed is left untouched instead of being rewritten with a new id
- Added: `nuget` field in `package.json`, `Precast~/` prebuilt assemblies, and the `IUiPresenter` contract hosts draw interfaces through
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
