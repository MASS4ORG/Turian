# Brick NuGet dependencies

A brick whose prebuilt assemblies need third-party libraries declares them in the `nuget` map of its `package.json`, package id to exact version. Platform-only packages go in `platformNuget` under their platform id. The map is the single source of truth; nothing else lists package versions by hand.

```json
"nuget": {
  "MASS4.Guinevere": "6.2.0",
  "MASS4.Guinevere.Styling": "6.2.0"
}
```

## Version policy

Versions are exact: floating versions such as `6.*` fail manifest validation, so restores are reproducible. When several loaded bricks request one package, the highest version within a major wins; different majors are a hard error naming both bricks and both versions. Ordering follows NuGet SemVer 2.0, prerelease labels included. Generated game projects receive the unified versions as `PackageReference` items.

## Restore and lock file

Loading bricks restores one merged stub project holding every loaded brick's map. The stub lives in `<project>/Bricks/NuGet/` and is generated; packages land in the project's hermetic cache at `<project>/.Cache/NuGet/packages` instead of the shared `~/.nuget/packages`. When a restore fails, loading that project's bricks fails with the restore's output in the error.

A project names its own package sources in `<project>/NuGet.config`, which the restore honours through NuGet's usual configuration lookup; when the project defines none, the machine's own sources apply.

The restore writes `packages.lock.json` beside the stub, and later restores run with `--locked-mode`. A locked restore cannot rewrite its lock file, so when the unified map no longer matches the lock's package set, the stale lock is deleted first and the restore writes a fresh one; an unchanged map restores locked and reproducibly. Commit a project's `packages.lock.json` to share one restore across machines. The stub project is generated, so do not commit it.

## Prebuilt payloads

`brick sync-precast <brick-folder>` restores the brick's map and copies its managed assemblies flat into `Precast~/lib`, writing a `.deps.json` beside every other assembly there so the editor resolves them. The copies are a generated artifact: never list third-party DLLs by hand in a brick `csproj`, and never commit them. `Precast~` is build output and the repository ignores it, so a brick is always used as its pack delivers it. Regenerate after changing the map.

A load skips the restore when a brick's payload already covers its map: `sync-precast` records what it copied in `Precast~/nuget-sync.json`, and `brick pack` carries that payload inside the packed file. A brick installed from a `.brick` therefore opens with no feed and no network, because its copies sit beside its assembly.

Only managed assemblies are copied: sync takes a package's `lib/` assemblies and leaves its native assets alone. A package that carries no managed assemblies, such as a native-assets package, is recorded with an empty file list, so it counts as covered with nothing to copy; its native runtime assets reach games through the unified `PackageReference` set at export time. Copying native payloads into bricks themselves needs extra handling later; do not promise it in brick READMEs.

## Engine checkouts

An engine checkout carries no committed copies, so the first editor launch after a fresh clone restores the builtin bricks' packages from the feed. CI runs `brick sync-precast` over the builtin bricks before the test suite.

## Game export

Exported games compile against the unified `PackageReference` set, so brick packages ship with the game. Server builds leave out `clientOnly` bricks and fail when their assemblies or packages would still ship.
