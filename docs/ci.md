# CI/CD (GitLab)

Maintainer notes for `.gitlab-ci.yml`. For the day-to-day "how do I submit a change" flow, see [CONTRIBUTING.md](../CONTRIBUTING.md); this page covers how the pipeline itself is built and kept fast.

## The CI image

Every job runs on `$CI_REGISTRY_IMAGE/ci:$ZIG_VERSION`, built from the repo-root `Dockerfile` (Ubuntu + Zig + curl/git-lfs/zip) by the `build_ci_image` job. This replaced installing Zig from scratch on every single job.

There's no suitable ready-made image for this: images that bundle Zig (e.g. `kassany/ziglang`) don't have a shell, which GitLab's executor needs to run `script:`/`before_script:` steps. Building our own, still Ubuntu-based, image is the workaround.

### First-time bootstrap

`build_ci_image` only runs automatically on pushes to the default branch that touch `Dockerfile` or `.gitlab-ci.yml` — every other pipeline just pulls the already-built image. The very first time (before the image has ever been pushed), or when testing a `Dockerfile` change from a feature branch, there's nothing to pull yet and the `test`/other jobs will fail with something like:

```
ERROR: Job failed: failed to pull image "registry.../ci:0.16.0" ...: manifest unknown
```

To fix it:

1. Open the pipeline in GitLab's UI.
2. Find `build_ci_image` in the `.pre` stage — it'll be greyed out with a manual "▶" (this is the escape hatch: `rules: - when: manual` for anything that isn't the default-branch auto-trigger).
3. Trigger it and wait for it to succeed (pushes `ci:$ZIG_VERSION` to the project's Container Registry — Settings → General → Visibility → Container Registry must be enabled).
4. Retry the job(s) that failed pulling the image (or retry the whole pipeline).

After that, the image exists in the registry and every subsequent pipeline — this branch and every other — just pulls it, until `Dockerfile` or `ZIG_VERSION` changes again.

## Caching

Two cache scopes:

- `zig-global-cache-v1` (`~/.cache/zig/`) — Zig's global cache (fetched `build.zig.zon` dependencies, the compiler's own cache). Not branch-specific, shared by every job/branch under one fixed key.
- `zig-cache-<ref>` (`.zig-cache/`) — the incremental project build cache, keyed per branch with `fallback_keys` back to the default branch's cache, so a fresh branch/MR isn't starting fully cold.

Zig 0.16 has no built-in cache garbage collection: every distinct set of inputs gets a new entry under `.../o/<hash>/`, and nothing is ever evicted automatically. Left unchecked this grows without bound (slower cache restore every pipeline). The `test` and `build_artifacts` jobs prune global-cache entries untouched for 14+ days before finishing — each entry is self-contained and content-addressed, so pruning one only costs a recompile if it's ever needed again, never a correctness issue.

Locally, the same growth happens in your own `.zig-cache/` and `~/.cache/zig/`. Instead of wiping the whole thing, an equivalent prune works fine as a lighter alternative to `rm -rf .zig-cache`:

```bash
find .zig-cache/o ~/.cache/zig/o -mindepth 1 -maxdepth 1 -type d -mtime +14 -exec rm -rf {} +
```

## Verbosity

`zig build` (bare) prints almost nothing to a non-interactive log until it finishes — the `test` job passes `--summary all` to `zig build`/`zig build test`/`zig build sdk` so the log shows a per-step breakdown (including timing) once each finishes, instead of going silent for minutes.
