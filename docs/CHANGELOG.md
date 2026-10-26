# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Added: rag dolls #64 #111;

## [3.5.0] - 2026-10-26

- Added: standalone physics bricks #64;
- Added: build and simulation settings diagnostics #324.
- Added: cached camera traversal and draw preparation.
- Changed: engine-managed node children and components.
- Fixed: brick-resolution errors stay actionable.

## [3.4.1] - 2026-10-25

- Fixed: cached brick bakes honor host compatibility.
- Fixed: malformed store answers name their endpoint #322.

## [3.4.0] - 2026-10-24

- Changed: bake twins are store variants.

## [3.3.2] - 2026-10-24

- Changed: publish NuGet packages.

## [3.3.1] - 2026-10-23

- Fixed: publish Gaya and Turian NuGet packages on release tags.

## [3.3.0] - 2026-10-23

- Added: asset store integration.
- Added: pluggable versioned saving system #89.
- Added: nuget support inside bricks #313.
- Changed: cameras, in-game UI, OBJ import, saves, GPU occlusion culling and the Windows looks moved to their own brick repositorie.
- Changed: major change in code style.
- Fixed: built-in studio bricks failing to load at startup
- Fixed: studio-only bricks failing to pack
- Fixed: and hidden folders such as `.github` being imported as assets.

## [3.2.0] - 2026-10-14

- Added: async scheduler foundation #88.
- Added: GPU instancing: identical mesh and material draws batch automatically #104
- Added: Gaya test project #77
- Fixed: `ktx validate` test in CICD #31

## [3.1.0] - 2026-10-14

- Added: camera background color controls the Game view and exported game's clear color.
- Added(android): the renderer survives surface loss and restore #246
- Added: KTX2 format #247
- Added: brick manifest platform allow-list and per-platform NuGet #248
- Added: application identifier sanitizer (for Java-package) #250
- Added: dotnet locator #251
- Changed: KTX2 replaces .amtex as the cooked texture container #31
- Changed: OAP and content discovery contracts #242 #243 #244
- Changed: platform-specific export steps #236 #252
- Changed: Vulkan renderer enhancements #237 #239 #245
- Changed: the MSAA sample count is a graphics setting #240
- Changed: UI brick resolves controller types from loaded assemblies #249
- Changed: OAP 1.1 packages (Zstandard, streamable chunked blobs, tombstones, aligned blobs) #133
- Changed: KTX2 and OAP libraries live in their own repositories and are referenced through local paths #133
- Removed: last .ammesh leftovers (catalog repair check, benchmark baseline)
- Fixed: asset loads reliably retry after failed metadata reads without retaining completed tasks.
- Fixed: mesh and task shader stages only when the device enables mesh shading #238
- Fixed: a KeyUp without a matching KeyDown no longer throws #241
- Fixed: `oap pack --store` stores assets instead of compressing them #133
- Fixed: install glslc in release-check
- Fixed: windowed and headless players quit on Ctrl+C or SIGTERM; a second interrupt terminates a hung game.

## [3.0.0] - 2026-10-11

- Changed (breaking): `.pss` color themes with hot reload #213
- Changed (breaking): Guinevere fluent effects.
- Changed (breaking): Gaya settings use class-level attributes as their sole configuration
- Changed (breaking): Wavefront OBJ import is an optional editor-only brick #47
- Changed (breaking): glTF 2.0 / GLB is now the internal mesh format
- Changed (breaking): Vulkan renderer moved out of core into a brick #62 #35
- Added: build profiles and platforms; headless dedicated server builds
- Added: `Assets/Project.dataasset` registers settings and build profiles and records the engine version
- Added: Asset browser revamp #68
- Added: Theme bricks from the command line #215
- Added: Bricks panel moved to Gaya with studio/project scopes #214
- Added: scene Frame Statistics HUD #59
- Added: temporary scenes
- Added: Icon sets (Mono, Color) #217
- Added: Material 3 as default theme; classic OS themes as bricks #212
- Added: in-game UI supports GPU rendering on Vulkan.
- Added: the `test` build target also emits a TRX report
- Added: Build Profiles window
- Improved: Panels API delegate tab-strip area.
- Improved: Studio renders on demand
- Improved: "Build & Run" and "export" skip the redundant NuGet restore already done during setup.
- Changed: Guinevere controls now use style sheets #218
- Changed: project solution generator revamped
- Fixed: local builds report the engine version from the csproj `Version`
- Fixed: shaders compile to SPIR-V 1.5, which Vulkan 1.2 devices load

## [2.3.1] - 2026-10-07

- Improved: improved asset caching for startup and "Build & Run".
- Recent projects use menu rows and configured titles with a fitted current-project control; native windows use Gaya or project-specific Turian titles and icons, and 
- Added: Scene and Game views share their configured empty sky. 
- Changed: Output filter for internal Studio messages.
- Changed: Disabled hierarchy branches appear dimmed.
- Fixed: release builds run again: the `release` job runners install glslc.

## [2.3.0] - 2026-10-06

- Added: multi-selection and multi-object editing #82

## [2.2.0] - 2026-10-06

- Added: tags and layers #91

## [2.1.0] - 2026-10-04

- Added: frustum culling CPU #8 GPU/HZB draft #52
- Added: multiplayer foundation #155
- Fixed: scene gizmos

## [2.0.0] - 2026-10-03

- Added: collapsable main menu and windowless
- Added: identity, content overlay and runtime save contracts #63 #197
- Added: BRICKS plugin system #79
- Added: dependency injection and DataAsset services #44
- Changed: struct Transform and allocation-free scene traversal #32 #132 #58
- Changed: send form generator to Guinevere: Autoformer #194
- Changed: chore(release): 2.0.0

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

[Unreleased]: https://github.com/MASS4ORG/Turian/compare/v3.5.0...HEAD
[3.5.0]: https://github.com/MASS4ORG/Turian/compare/v3.4.1...v3.5.0
[3.4.1]: https://github.com/MASS4ORG/Turian/compare/v3.4.0...v3.4.1
[3.4.0]: https://github.com/MASS4ORG/Turian/compare/v3.3.2...v3.4.0
[3.3.2]: https://github.com/MASS4ORG/Turian/compare/v3.3.1...v3.3.2
[3.3.0]: https://github.com/MASS4ORG/Turian/compare/v3.2.0...v3.3.0
[3.2.0]: https://github.com/MASS4ORG/Turian/compare/v3.1.0...v3.2.0
[3.1.0]: https://github.com/MASS4ORG/Turian/compare/v3.0.0...v3.1.0
[3.0.0]: https://github.com/MASS4ORG/Turian/compare/v2.3.1...v3.0.0
[2.3.1]: https://github.com/MASS4ORG/Turian/compare/v2.3.0...v2.3.1
[2.3.0]: https://github.com/MASS4ORG/Turian/compare/v2.2.0...v2.3.0
[2.2.0]: https://github.com/MASS4ORG/Turian/compare/v2.1.0...v2.2.0
[2.1.0]: https://github.com/MASS4ORG/Turian/compare/v2.0.0...v2.1.0
[2.0.0]: https://github.com/MASS4ORG/Turian/compare/v1.2.0...v2.0.0
[1.2.0]: https://github.com/MASS4ORG/Turian/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/MASS4ORG/Turian/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/MASS4ORG/Turian/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/MASS4ORG/Turian/releases/tag/v1.0.0
