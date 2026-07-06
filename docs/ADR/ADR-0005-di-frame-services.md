# ADR 0005: Dependency Injection — Frame & Services

**Status**: Implemented

## Context
Engine subsystems (Input, SceneManager, Profiler, user plugins) need access to each other at runtime. Traditional approaches — globals, singletons, manual plumbing — create coupling and testability problems.

## Decision
- **No static globals**. All dependencies are explicit.
- **`engine.Frame`** — bundles per-update services into a single struct passed to scripts and systems each tick. Contains Input, SceneManager, Profiler, Application, Services, allocators, timestamp.
- **`engine.Services`** — type-keyed registry (`register(T, *T)` / `get(T)`), MAX=64 linear scan. User extensions attach here. Plugins register via generated `entry(&g_services)`.
- **Lifecycle injection**: all hooks dispatch by param TYPE. Provider registers in `awake`, consumer calls `frame.service(T)` in `start`/`update`.
- **SOAP deferred**: Frame/Services is the current model. Unity "SOAP" (event channels, shared-variable assets) is complementary layer deferred to post-MVP.

## Consequences
- All engine consumers take deps explicitly — no surprise state.
- `configureInput` hook retained as code-first escape hatch (for projects that skip `.inputactions` assets).
- MAX=64 linear scan is adequate for current scale; SOAP will address larger-scale decoupling later.
