# ADR 0002: Package Registry & Store

**Status**: Implemented (MVP)

## Context
Packages need a storage location for multiple versions and a mechanism to discover and install packages from remote registries. The project also bundles vendored asset packages locally. Base for a future "Asset Store".

## Decision
- **Centralized store**: `~/.cache/turian/packages/<name>/<version>/`. Multiple versions coexist. `TURIAN_PACKAGE_HOME` / `XDG_CACHE_HOME` override.
- **Vendored packages**: `<project>/packages/` wins on name clash over central store.
- **Registry API**: npm/OpenUPM-compatible subset — `GET /<name>`, `GET <dist.tarball>`, `GET /-/v1/search`. Turian-specific fields under `turian` key in `package.json`.
- **Planned OEP extraction**: engine-agnostic Zig core extracted to `open-engine-package` (OEP) repo, mirroring `open-asset-package` architecture.

## Consequences
- Compatible with Verdaccio, Artifactory, GitHub Packages as registries.
- Store is read-through cache — `turian install` fetches missing versions.
- OAP stays asset-only; OEP will be the code/runtime analogue.
