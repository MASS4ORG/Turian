# Model sources and cooked geometry

Turian imports model sources in the editor and stores geometry in AMMESH. `ModelAsset.GetContent` reads only AMMESH; exported games do not parse OBJ, FBX, glTF or GLB authoring sources. glTF/GLB and FBX importers remain built into the editor. Wavefront OBJ support comes from the optional [OBJ editor brick](../Turian/Packages/org.mass4.turian.obj/README.md), which documents installation and migration without changing asset IDs.

## Why AMMESH exists

AMMESH version 1 is a geometry container with a binary manifest and payload. It stores the engine's interleaved `Vertex` layout, optional secondary UV stream, shared uint32 indices, mesh/submesh ranges, material slot indices and precomputed bounds. Its writer runs in the editor; its reader has no model parser dependency. Matching vertex streams can be decoded with `MemoryMarshal.Cast`, avoiding conversion of each attribute at load time. The current reader still reads the file into memory and allocates vertex/index arrays: this is not a zero-copy or memory-mapped loader.

Those properties justify cooking source models into a predictable runtime representation. They do not establish that the container must be proprietary. The runtime currently has no AMMESH meshlet, platform-specific packed geometry, compression, skinning or animation chunks that would provide a strong format-specific justification. No comparative load-time, memory or file-size benchmark establishes an AMMESH advantage over a normalized GLB.

## Could cooked GLB replace it?

Yes. [The glTF 2.0 specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#motivation-and-design-goals) explicitly targets runtime efficiency and binary buffers suitable for GPU upload. Its buffer views and accessors describe offsets, stride and component types, including interleaved vertices and uint32 indices. A cooked GLB could therefore retain the engine's preferred vertex layout. It need not inherit all the variability of arbitrary external glTF files: the editor can normalize source assets into a documented runtime profile.

That profile would have to define triangle primitives, attribute types/layout, required attributes, index representation, material slot mapping, mesh/submesh bounds, stable mesh identity and the relationship between GLB geometry and Turian's separately serialized assets. Standard accessor bounds can supply position bounds; aggregate or range-specific engine metadata can be derived during import or recorded in `extras` or an extension. Skinning and morph targets would benefit from the existing glTF schemas if those features are implemented. Turian scene/component serialization and asset references still need their own engine contracts.

Using GLB would replace the custom manifest reader/writer and gain standard validators and inspection tools. It would also require a runtime reader, an editor writer, validation of the cooked profile and migration of the export/OAP classification and existing cooked assets. The existing editor glTF importer normalizes external models and constructs engine assets; moving that entire importer into Engine.Core would bring unnecessary editor behavior and dependencies into games. A focused BCL reader is possible, or a library can be evaluated with its dependency and deployment costs made explicit.

## Recommendation

Keep AMMESH compatible for the OBJ dependency extraction. For a separate format decision, prototype a normalized GLB writer/reader, compare actual source and dependency size, and benchmark identical geometry for cold/warm loads, allocations, file size and upload preparation. Prefer GLB if it reduces maintenance without a material measured regression. Retain AMMESH only if measurements or concrete engine-specific cooked data justify owning a separate format. The current evidence supports evaluating GLB; it does not support claiming that glTF is unsuitable for internal use.
