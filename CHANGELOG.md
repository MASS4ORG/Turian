# Changelog

## [2.2.0] - 2026-07-21

### Features
- feat: import glTF/GLB node hierarchy as mesh + prefab sub-assets #8
- feat: alpha/additive blend modes, cull mode, and alpha-mask cutout for materials #26
- feat(editor): shortcut binding model and command registry #14

## [2.1.0] - 2026-07-20

### Features
- feat: sRGB/linear color management with ACES tonemap #27

## [2.0.0] - 2026-07-18

### Breaking Changes
- feat!: per-submesh materials break TMSH v1 mesh assets #45

### Features
- feat!: per-submesh materials break TMSH v1 mesh assets #45

### Other
- refactor(ci): move pipeline logic into Zig release tool subcommands

## [1.17.0] - 2026-07-18

### Features
- feat: DDS texture import (BC1-BC7, sRGB tagging, normal-map Y-flip) #134
- feat: FBX mesh and "scene hierarchy" import #133

## [1.16.0] - 2026-07-17

### Features
- feat: build output folder (.public), asset-ref project settings, project icon #131 #86
- feat: localization #36 #123

## [1.15.0] - 2026-07-15

### Features
- feat: .uitheme asset, Studio theme #87 #104 #122

## [1.14.0] - 2026-07-14

### Features
- feat: Output/Log panel with levels, filtering, and stack traces #23
- feat: Panel API

### Other
- chore: favicon to window and about dialog #89

## [1.13.0] - 2026-07-13

### Features
- feat: fixed-width asset browser guid cells #84
- feat(studio): build and run Studio on Windows (D3D12 fallback,cross-platform DynLib) #119
- feat(studio): asset browser clickable breadcrumb and cascaded Create menu (#81 #68 #85 #72)
- feat(studio): add Grid+Tree and Tree Only navigation modes to Asset Browser #79 #80 #83
- feat(studio): add unified Settings editor with category sidebar and search (#88)
- feat: add Font as a first-class asset type (#109)
- feat: In-game GUI enhancements, screenshot verification, and GameEvent channel assets #103, #107

## [1.12.2] - 2026-07-09

### Other
- chore: change the license to MPL v2

## [1.12.1] - 2026-07-07

### Other
- docs: Architecture Decision Records (ADRs)

## [1.12.0] - 2026-07-06

### Features
- feat: In-game GUI! #92 #93 #94 #95 #96 #97 #98 #99 #100 #101 #102 #103

## [1.11.0] - 2026-07-03

### Features
- feat: Asset preview API for Inspector & Asset Browser (#19 #25)

### Bug Fixes
- fix(studio): hard-exit after cleanup to avoid crash on window close

## [1.10.0] - 2026-07-02

### Features
- feat: Plugin & Package System (#4 #20 #56 #57 #58 #59 #60 #61 #62 #64) — manifest, modules and plugin runtime registration

## [1.9.0] - 2026-06-27

### Features
- feat: Remote debug and MCP server #2 #49 #50 #51

## [1.8.0] - 2026-06-25

### Features
- feat(studio): In-engine profiler #35

## [1.7.0] - 2026-06-24

### Features
- feat(studio): MDI (multiple asset edit via tabs) #1

### Bug Fixes
- fix(ci): macOS and Windows builds

### Other
- refactor(studio): import DVUI as `gui` namespace

## [1.6.0] - 2026-06-23

### Features
- feat: Gizmos API + interactive transform gizmo #3

## [1.5.0] - 2026-06-19

### Features
- feat: prefabs system #32

## [1.4.0] - 2026-06-16

### Features
- feat(studio): editor UX cleanup — inspector, asset browser, rename, play controls #44

## [1.3.0] - 2026-06-15

### Features
- feat: materials and textures from glTF/GLB #16 #28 #5 #18

## [1.2.0] - 2026-06-13

### Features
- feat: in-editor Play mode! #31
- feat: Scene references #43
- feat: Project settings #13 and Scene Manager API #22

## [1.1.0] - 2026-06-12

### Features
- feat: input action maps, gamepad, DI via engine.Frame (#10 #12)
- feat: DataAssets (ScriptableObject) system #11

### Other
- chore(ci): allow `v*.*.*-*` as protected tags

## [1.0.0] - 2026-06-11

### Breaking Changes
- feat!: first commit!

### Features
- feat!: first commit!

## [1.0.0] - 2026-06-11

### Features

- Initial release of Turian Engine — a component-based 3D game engine and editor built entirely in Zig.
