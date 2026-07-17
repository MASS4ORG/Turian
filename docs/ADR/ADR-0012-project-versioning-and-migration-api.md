# ADR 0012: Project Versioning & Migration API

**Status**: Proposed — #137

## Context

`project.json` already carries a `turian_version` field (`ProjectConfig.zig`), but nothing reads it for compatibility — it's round-tripped, never compared against the running engine. Breaking changes to on-disk project data (scene JSON shape, component ABI, cooked-asset formats) have so far been handled ad hoc: #45's `SceneMeshRenderer.material_guid` → `material_guids` rename kept the old field as a deprecated, auto-migrated-on-load fallback with a log warning, plus a one-off `turian-cli migrate` command to batch-rewrite scene files. That pattern works for "old data still loads, just log about it," but has no home for changes that aren't expressible that way (renaming/moving files on disk, a cooked-format major version bump that needs a project-wide re-cook, anything requiring the user's attention before proceeding at all), and gives the user no warning *before* opening a project that a version gap exists.

This ADR defines a Unity-style "project older/newer than the engine" check on project open, and the migration-routine framework it runs.

## Decision

**Version comparison & trigger.** Compare `project.json`'s `turian_version` (semver `major.minor.patch[-rc.N]`) against the running engine's version at project-open time, in both Studio and the CLI.
- Major mismatch: always triggers the flow.
- Minor mismatch: triggers only if enabled in Settings (`project.version_check_minor`, default **on**).
- Patch/prerelease differences: never trigger.

**Direction-dependent UX.**
- **Project older than the engine** (needs migrating forward): a dialog listing every pending migration between the project's recorded version and the engine's — each with its `summary` and, if non-empty, `manual_steps` — so the user knows what will run and what they may still need to do by hand *before* committing. Three choices: **Cancel** (don't open), **Update** (bump `turian_version` in `project.json` without running any migration — for a project already fixed up by hand), **Migrate and Update** (run every pending migration in order, then bump). Multiple versions behind runs as one operation, not one-by-one, but the pre-flight list still enumerates every migration that will run.
- **Project newer than the engine** (opened with an older Studio than it was last saved with): **Cancel**, or a warning-styled **Open Anyway** — nothing to migrate going backward, just an explicit "you're on an older Studio" signal instead of silent, possibly-broken behavior.
- **CLI**: the same three-way (older) / two-way (newer) choice as an interactive prompt, plus `--yes` (accept the recommended action — "Migrate and Update" / "Open Anyway") and `--no` (the safe/declining action — "Cancel") for unattended CI use, and `--dry-run` to print the pending-migration list without applying anything. `turian-cli migrate <project-path> [--yes|--no] [--dry-run]` is the single entry point, covering both the project-version flow and any standalone data-format fixups that don't need a version bump (folding in the scene `material_guid` fixup shipped as its first pending-migration content, not a separate command).

**Migration shape & discovery.** `editor/project/migrations/`, one file per version bump:

```zig
// editor/project/migrations/v0_17_0.zig
pub const migration = MigrationApi.Migration{
    .to_version = .{ .major = 0, .minor = 17, .patch = 0 },
    .summary = "Re-saves mesh_renderer components using the old single material_guid field.",
    .manual_steps = "",
    .idempotent = true,
    .run = run,
};
fn run(ctx: MigrationApi.Context) !void { ... }
```
Discovery is a single hand-maintained list (`editor/project/migrations/root.zig`: `pub const all = [_]Migration{ @import("v0_17_0.zig").migration, ... };`), matching this codebase's existing explicit-registration convention (the `Component` union, `editor/components/root.zig`'s builtin list) rather than comptime-scanning or attribute-based discovery — Zig has no attribute reflection, and a name-convention scan (à la Drupal) would need directory listing at comptime with no real benefit here since the list is short and changes rarely. A test asserts `all` is sorted ascending by `to_version`.

**Runner.** `MigrationApi.pendingFor(current) []const Migration` (the ascending slice of `all` above `current`, up to the engine's version); `MigrationApi.run(migrations, ctx) RunResult` executes them in order, stopping at the first failure and reporting which one and why (partial application is possible — the project's version isn't bumped until every migration in the batch succeeds, so a re-run resumes from the failure point, not from scratch).

**Idempotency.** `Migration.idempotent: bool` is advisory metadata, shown in the pre-flight list and `--dry-run` output. The runner doesn't skip or reorder based on it — it can't safely infer whether a specific project already had a migration applied beyond version bookkeeping — but it refuses to silently re-run a `idempotent = false` migration against a project whose recorded version is already `>= migration.to_version` (which shouldn't happen in the normal flow, but guards a manually-edited `project.json` or an interrupted prior run) and instead surfaces `Migration`'s message about what re-running unsafely would do.

**One implementation, two front-ends.** `editor/project/MigrationApi.zig` (or similar) is called both by `turian-cli migrate` and by Studio's open-project version-check dialog (plus a manual "Project > Check for Migrations" action) — no logic duplicated between CLI and GUI.

## Consequences
- `turian_version` becomes load-bearing: opening a newer-format project with an older Studio (or vice versa) now surfaces explicitly instead of silently "mostly working" or silently dropping data (as `material_guid` did before #45's fallback was added).
- CI pipelines that script `turian-cli build`/`import`/etc. against a project need to pass `--yes` (or accept the default once one is chosen) to avoid hanging on an unattended prompt — `migrate` gains this flag first, every other CLI command that implicitly opens a project should route through the same version-check + flag before this ships.
- Every future breaking scene/asset/component format change gets a required home (a migration file with a mandatory `summary`) instead of an ad hoc fix — the deprecated-field-plus-warning pattern (`material_guid`) stays the right tool for "old data keeps loading transparently," and now becomes the *implementation* of one entry in this registry rather than a parallel, undiscoverable mechanism.
- issue #137 `size:XL`-scale: version-compare plumbing, the migration runner + registry, a Studio dialog, and CLI prompt/flag handling are each independently   substantial; implementation should land in phases (runner + CLI first, Studio dialog after) rather than as one change.
