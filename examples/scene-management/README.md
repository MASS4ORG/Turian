# Scene Management Demo

Demonstrates the **Scene Management API** (issue #22): asynchronous/additive/
persistent scene loading, an active-scene concept, and runtime scene switching
driven from a script.

Unlike a Turian *scene-as-prefab* (which you instantiate into a node tree), the
`engine.SceneManager` treats scenes the way Unity's `SceneManager` does: named,
addressable units that are **loaded** and **unloaded** as a whole.

## What it does

* `assets/scene-boot.json` is the **bootstrap scene**: a camera, a light, and the
  `SceneDirector` script. It is configured as the boot scene in
  `assets/project.projectsettings` (`first_scene`).
* On `start`, `SceneDirector` marks the bootstrap scene **persistent** (so the
  camera/light/director survive level transitions) and **additively** loads
  `scene-level-a.json`.
* At runtime the director responds to keys:
  * **1** — switch to Level A (`single` — replaces the current level)
  * **2** — switch to Level B (`single`)
  * **3** — add Level B alongside the current level (`additive`)
  * **4** — unload Level B

The persistent bootstrap scene is the `SceneManager` analogue of keeping a
`DontDestroyOnLoad` object across loads: `single` loads unload every
*non-persistent* scene, so the levels swap while the camera/director remain.

## Run it

```sh
zig build cli -- build examples/scene-management
examples/scene-management/.cache/zig-out/bin/game
```

Headless (no window) it prints the boot + load lifecycle:

```
[Turian] Booted scene 10000000-0000-4000-8000-000000000001
[SceneDirector] Bootstrap ready — loading Level A (additive)
[SceneDirector] 2 scene(s) loaded; keys 1-4 switch/add/unload levels
```

## Notes

* Scene ids are hard-coded GUID strings in `SceneDirector.zig`. Component fields
  cannot yet hold scene asset references (`TypedAssetRef` hydration is a pending
  editor feature), so a shipping game would expose these as inspector-edited refs.
* Scene-change requests from scripts are **deferred** to the end of the frame
  (`SceneManager.requestLoad` / `requestUnload` → `flushRequests`) so a script can
  never unload the scene whose nodes the host is mid-iteration over.

See `docs/plans/scene-management.md` for the architecture and the engine API.
