# 3D Model Materials

Demonstrates **model + material import** and the shared GPU renderer. The scene
shows three objects side by side, each exercising a different path:

| Object | Format | Material |
|--------|--------|----------|
| Cube | `cube.obj` (Wavefront) | hand-authored material with a **KTX2** (Basis→BC7) albedo |
| Water Bottle (left) | `WaterBottle.gltf` | generated PBR, **external** `.png` maps |
| Water Bottle (right) | `WaterBottle-glb.glb` | generated PBR, **embedded** maps extracted on import |

Open it in Turian Studio via **File → Open Project…** and select this folder.

## What it shows

- **Material generation** — importing a glTF/GLB yields a PBR `.material` per
  glTF material, mapping metallic-roughness to the built-in PBR shader.
- **External vs embedded textures** — the `.gltf` references sibling `.png`
  files (their own swappable assets); the `.glb` carries its images inside, and
  the importer extracts them into cache texture sub-assets.
- **Generated sub-assets** — select a bottle in the asset browser; the inspector
  lists its generated material under *Generated Assets*. Click it to tweak the
  colour — edits persist across reimports.
- **Import settings** — select a texture or model to see its import settings
  (texture type, color space, mipmaps, compression / import materials, scale).
- **One renderer** — the editor viewport and a built game (`turian-cli build`)
  render through the same SDL3-GPU renderer (PBR + shadows; KTX2/BCn supported).

## Scaling up

Drop a `.gltf`/`.glb` (Sponza, Bistro, …) into `assets/models/`, open the
project, and add it to a scene — Studio scans, imports, and cooks it into the
canonical mesh + materials automatically.

> Binary assets are tracked with **Git LFS** — run `git lfs install` once before
> cloning if you need them.
