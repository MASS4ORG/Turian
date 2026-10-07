# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Added (breaking): Studio color themes are `.pss` stylesheets. The 14 built-ins ship as embedded sheets with identical colors, and every token derives from three seeds (`$base`, `$accent`, `$contrast`) in `gaya.base`. Sheets dropped into `~/.gaya/themes` appear in View → Themes and Settings and reload on save; a broken sheet keeps the last valid theme and logs `file:line:col`. `~/.gaya/theme.user.pss` overrides any theme. New tokens: success/info/hint, selection, scrim, log levels, ANSI 0–15 and version-control states. Plugins declare their own tokens (`IPluginContext.Themes`), and the Scene view's gizmo and orientation colors are now themeable. Settings store theme ids (old names still load). Migration: `StudioTheme` becomes `ThemeTokens` (`ThemeTokens.Current`); `IThemeService.Apply`/`Preview`/`Themes`/`CommittedName` become `ApplyColorTheme`/`PreviewColorTheme`/`ColorThemes`/`CommittedColorTheme`; `Register(StudioTheme)` is obsolete for one release. #213

## [2.3.1] - 2026-10-07

- Improved: Studio startup and project switching show the workbench with a centered loading card, active phases, asset counts and elapsed time. Unchanged assets retain their imported files and child indexes across restarts; generated projects preserve timestamps, either assembly slot can supply cached code, and Build & Run copies only changed assets. Recent projects use menu rows and configured titles with a fitted current-project control; native windows use Gaya or project-specific Turian titles and icons, and Scene and Game views share their configured empty sky. Output separates Information and internal Studio messages and offers source files through the OS default application and copy actions, Inspector selection avoids diagnostic spam, and disabled hierarchy branches appear dimmed; dock tabs, compact menus, and initial window placement are corrected in Guinevere. OBJ/FBX imports preserve upward Y to prevent inverted models; caches regenerate on import, and scenes that compensated with negative Y scale should remove that compensation.
- Fixed: release builds run again — the skip-shaders flag now reaches MSBuild, so the tag ships the committed .spv, and the release runners install glslc anyway.

## [2.3.0] - 2026-10-06

- Added: multi-selection and multi-object editing #82

## [2.2.0] - 2026-10-06

- Added: tags and layers

## [2.1.0] - 2026-10-04

- Added: frustum culling CPU #8 GPU/HZB draft #52
- Added: multiplayer foundation #155
- Fixed: scene gizmos

## [2.0.0] - 2026-10-03

- Added: collapsable main menu and windowless
- Added: collapsable main menu and windowless
- Added: identity, content overlay and runtime save contracts #63 #197
- Added: add the BRICKS plugin system #79
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

[Unreleased]: https://github.com/MASS4ORG/Turian/compare/v2.3.1...HEAD
[2.3.1]: https://github.com/MASS4ORG/Turian/compare/v2.3.0...v2.3.1
[2.3.0]: https://github.com/MASS4ORG/Turian/compare/v2.2.0...v2.3.0
[2.2.0]: https://github.com/MASS4ORG/Turian/compare/v2.1.0...v2.2.0
[2.1.0]: https://github.com/MASS4ORG/Turian/compare/v2.0.0...v2.1.0
[2.0.0]: https://github.com/MASS4ORG/Turian/compare/v1.2.0...v2.0.0
[1.2.0]: https://github.com/MASS4ORG/Turian/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/MASS4ORG/Turian/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/MASS4ORG/Turian/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/MASS4ORG/Turian/releases/tag/v1.0.0
