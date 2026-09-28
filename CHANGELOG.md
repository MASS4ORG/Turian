# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Added: nested prefabs, variants, and prefab override tracking with live refresh (#84, #185)
- Added: undo/redo with per-document history, covering scene edits, prefab overrides, and asset operations (#81)
- Added: themed control palette and updated icon set
- Fixed: start the asset importer when a project opens
- Changed: prefab instances now save compactly as differences from their prefab; scenes with prefab instances saved before this change need re-saving

## [1.0.1] - 2026-09-25

### Fixed
- publish successful platform artifacts even when one build fails
## [1.0.0] - 2026-09-25

### Added
- generated DataAsset serializers, preloading, observable data and complete direct references
- direct Node, Component and DataAsset fields are references
- typed DataAssetReference and IAssetLoader.LoadContentAsync
- share one cached DataAsset payload per asset
- First commit!

### Fixed
- pass git arguments safely when tagging
- invalidate user code cache when engine assembly changes

### Changed
- removed legacy code and some translations
