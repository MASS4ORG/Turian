# ADR 0016: Background Task System

**Status**: Implemented.

## Context

#151 asked for one aggregate progress bar when a task has children, instead of
a taskbar that lists every subtask. The wider problem behind it: compiling user
scripts takes minutes on Windows, and while it runs the editor gives almost no
usable signal. It does not say what is running, how far along it is, how long it
has taken, what is unsafe to touch, or what happens if the user presses the same
button again. The same information is missing from the CLI and from tools
driving the editor over the debug protocol.

The pre-existing system (`editor/tasks/TaskManager.zig`, `studio/main-window/TaskBar.zig`)
had a flat task list, one fraction per task, cooperative cancellation, and a
studio job runner that allowed exactly one job at a time and popped a modal
dialog for any second request. Parent/child, ordering, and locking did not
exist; the "parent/child relationship already exists" premise in #151 was not
actually true of the code.

## Decision

### Aggregation: nested phases, not per-item tasks

A task carries a `parent_id`. `editor/tasks/TaskTree.zig` rolls children up into
the parent: aggregate progress is the **weighted mean** of child progress, and
the parent's effective status is `running` while any child is, otherwise the
worst terminal outcome among them.

Weights matter because build phases are wildly unequal — a four-minute compile
next to three two-second steps. Unweighted, the bar would sit at 50% for
essentially the whole build. `GameBuild` declares 1 / 3 / 12 / 1 for
generate / package / compile / copy.

An operation also declares its **planned total child weight** up front
(`Progress.plan`). Without it the aggregate divides by the phases opened *so
far*, so opening the heavy compile phase drags the parent backwards — and a bar
that walks backwards reads as a bug.

Batch work does **not** spawn one task per item. A thousand-asset import would
swamp a fixed-size registry with entries nobody reads, and the registry is a
flat array by design. Instead a task publishes **item counters**
(`Progress.units(done, total)`), which drive its fraction and give the display a
literal "142 / 1035". Child tasks are reserved for *structurally distinct*
phases, of which there are a handful.

### Parallelism

The registry was already thread-safe and multi-task; the single-job restriction
lived in the studio runner. That is now a fixed table of `MAX_PARALLEL = 2`
worker slots. A job starts when its dependencies have landed and no *running*
task holds a capability it needs.

Presentation deliberately does not scale with parallelism:

- **Task bar** shows one row — the most disruptive active root
  (`build > package > compile > import > scan > generic`), with `+N more` for
  the rest. The expanded list shows every root; nested phases sit behind a
  per-root expander, collapsed by default.
- **CLI** (`editor/tasks/TaskReporter.zig`) renders the same rollup as
  `[ 42%] Build game — Compiling game (12.3s)`, suppressing repeated lines, with
  `dump` printing roots plus indented children.

### Dependencies

A task may declare `deps` and starts in the `blocked` state. `TaskManager.tick`
promotes it to `queued` once every dependency reaches a terminal state, and
**cancels** it if any dependency failed — a build must not run on a
half-imported project.

Two real orderings now use this rather than blocking the UI thread:

- Script reflection depends on the in-flight asset import (compiling user
  scripts reads the project's imported assets). These previously raced.
- A game build depends on whichever import/compile jobs are in flight. This
  replaces the `waitForImport` + `waitForReflect` pair that froze the editor at
  the moment the user pressed Build.

Because a build's inputs are snapshotted when it *starts* rather than when it is
submitted, a reflect job it waited on cannot leave it with stale component
definitions.

### Locking: per capability, never the whole editor

A task declares the capabilities it holds while active — `assets`, `scripts`,
`scene`, `project` — and the UI disables just the actions those cover. A long
script compile still leaves scene editing and saving available; only Play is
grayed, since it would run stale code. The blocked control's tooltip names the
task responsible, because a grayed button with no explanation is the most common
"is this broken?" moment.

Whole-editor modality was rejected: on Windows these operations run for minutes,
and there is nothing about a script compile that makes moving a transform
unsafe.

### Retention

Completed roots retire on their own after `DEFAULT_AUTO_CLEAR_MS` (5 s), taking
their children with them. Failed and cancelled tasks are **sticky** — they carry
a diagnosis nobody has read yet — and leave via the list's "Clear finished"
button. Retention is driven by `tick(now_ms)` rather than an internal clock, so
the registry stays free of `Io` and the behaviour is directly testable.

### Duplicate submissions

Submissions carry a key and a policy:

| Policy | Behaviour when an identical task is in flight |
| --- | --- |
| `queue` | always create another |
| `coalesce` | reuse it, and flag exactly one rerun on completion |
| `drop` | ignore, returning the in-flight id |
| `restart` | cancel the incumbent and queue a fresh one |

"Reimport All" and "Build Game" use `coalesce`. A burst of clicks therefore
costs one extra run — not one run per click, and not a modal dialog telling the
user they are holding it wrong.

### Machine-readable exposure

`World.tasks` carries `introspect.TaskView` records (id, parent_id, kind,
status, label, aggregate progress, units, elapsed, held locks), served by the
`tasks.list` debug method and the `list_tasks` MCP tool. A tool that triggers an
import can now tell whether the result it is reading has landed, and a `scripts`
lock tells it that compiled user code is stale.

## Consequences

- `Progress` grew optional `beginChild` / `endChild` / `units` / `planChildren`
  vtable entries. They default to null and degrade to no-ops, so existing sinks
  are unaffected and operations need no capability checks.
- Operations describe their own shape (phases, item counts, planned weight)
  without depending on the registry that renders them, so the CLI and the editor
  report a run identically.
- The task bar's compact row does not grow with the number of tasks, which was
  the point of #151 — but the ceiling is one visible task plus a count, so
  anyone wanting the detail has to expand the list.
- `MAX_PARALLEL = 2` is a deliberate floor, not a scheduler. Real fan-out (a
  worker pool importing assets concurrently) would need per-asset locking in
  `AssetDatabase` first.
