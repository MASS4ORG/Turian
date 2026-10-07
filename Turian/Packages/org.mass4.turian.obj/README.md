# Wavefront OBJ importer

This optional editor brick imports `.obj` sources into `primary.ammesh`. It provides no runtime assemblies. Its parser dependency ships only in `Precast~/editor`, and exported games read the cooked AMMESH geometry.

## Installation and migration

Run `turian-cli brick add <project> org.mass4.turian.obj builtin:org.mass4.turian.obj`, or add `"org.mass4.turian.obj": "builtin:org.mass4.turian.obj"` to the project's `Bricks/manifest.json` dependencies. Reopen the project after changing its bricks. In a source checkout, build the solution first to generate this brick's editor payload.

Keep existing `.obj.meta` files: their asset type, IDs and import settings stay valid. The bake version remains 3 and the AMMESH container remains version 1, so valid cooked OBJ caches need no rebuild. Source changes and missing caches require this brick. Without it, the editor reports an unsupported OBJ import with installation/conversion guidance instead of copying OBJ into runtime content.

To migrate away from OBJ, export the source to glTF/GLB or FBX, move its `.meta` alongside the replacement source, and retain its asset ID. Verify the reimport and scene references before deleting the old source. Direct calls to the parser now use `Turian.Editor.Obj.ObjModelBuilder` from this editor brick.

## Scope

The importer preserves the existing OBJ geometry behavior: vertex colors, source Y coordinates, UV conversion and vertex deduplication. Faces must contain normals and texture coordinates, and authored faces should be triangulated. Material libraries and object hierarchies are not imported.
