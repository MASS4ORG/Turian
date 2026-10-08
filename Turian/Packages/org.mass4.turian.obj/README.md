# Wavefront OBJ importer

This optional editor brick imports `.obj` sources into `primary.glb` through the editor's shared Assimp converter. It provides no runtime assemblies or separate OBJ parser dependency. Exported games read glTF 2.0 geometry.

## Installation and migration

Run `turian-cli brick add <project> org.mass4.turian.obj builtin:org.mass4.turian.obj`, or add `"org.mass4.turian.obj": "builtin:org.mass4.turian.obj"` to the project's `Bricks/manifest.json` dependencies. Reopen the project after changing its bricks. In a source checkout, build the solution first to generate this brick's editor payload.

Keep existing `.obj.meta` files: their asset type, IDs and import settings stay valid. Importer version 4 invalidates AMMESH caches and creates GLB geometry from the source. Rebuild exported games after reimporting. Without this brick, the editor reports an unsupported OBJ import with installation/conversion guidance.

To migrate away from OBJ, export the source to glTF/GLB or FBX, move its `.meta` alongside the replacement source, and retain its asset ID. Verify the reimport and scene references before deleting the old source. Code that called `ObjModelBuilder` should use the shared `AssimpModelConverter.ConvertToGlb` and `GltfModelReader` APIs.

## Scope

The importer preserves the existing OBJ geometry behavior: vertex colors, source Y coordinates, UV conversion and vertex deduplication. Faces must contain normals and texture coordinates, and authored faces should be triangulated. Material libraries and object hierarchies are not imported.
