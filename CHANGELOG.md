# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
