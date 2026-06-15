# Examples

Each subdirectory is a self-contained Turian project.  Open any of them in
Turian Studio via **File → Open Project…**.

| Example | Focus |
|---------|-------|
| [`basic-project/`](basic-project/) | Scripting lifecycle, component fields, object/asset references |
| [`scene-management/`](scene-management/) | Multiple scenes and scene transitions |
| [`3d-model-materials/`](3d-model-materials/) | glTF/GLB import generating PBR materials + textures |

## Planned examples

- `physics/` — RigidBody and Collider components
- `audio/` — AudioSource component
- `ui-overlay/` — Rendering a 2D HUD with the software renderer

## Adding binary assets

Large binary files (meshes, textures, audio) are tracked with **Git LFS**.
Run `git lfs install` once before cloning if you need them.
