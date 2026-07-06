# ADR 0001: Package System

**Status**: Implemented

## Context
Turian needs a plugin and package system allowing users to distribute assets, source code, native libraries, and editor extensions. Base for a future "Asset Store". The system must support versioning, dependency resolution, and integration with the Zig build system.

## Decision
- **`turian-package.json`** manifest at package root alongside `build.zig.zon`. JSON (not ZON) for consistency with project conventions.
- Fields: `name` (reverse-DNS, required), `version` (semver), `author`, `description`, `license` (SPDX), `engine_compat` (semver range), `types` (asset|source|native|plugin), `assets`, `modules`, `native`, `plugin`, `dependencies`.
- **Dependency resolution delegated to Zig PM**. `project.json` is source of truth; `build.zig.zon` generated from it. Turian owns engine integration metadata, not transitive resolution.

## Consequences
- No custom resolver — reuse Zig's proven dependency model.
- Plugin registration reuses `engine.Services` via generated `@import(module).entry(&g_services)`.
- Zig package `.name` must be bare identifier — dots/leading digits replaced with `_`.
- Fingerprint uses `Crc32(name)<<32 | stable-id`.
