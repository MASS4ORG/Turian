# Turian Engine (`engine/`)

The **engine** module is the core of the Turian game engine. Its entry point is `root.zig`, which exports the `Engine` namespace — a collection of fundamental types (`Vec2`, `Vec3`, `Time`, `Project`, `Transform`, `GameObject`, `Component`), all built-in component types, reference types (`GameObjectRef`, `ComponentRef`, `AssetRef`), the `api` submodule, and the `scene` namespace (constants like `MAX_OBJECTS` / `MAX_COMPONENTS`). The engine has no mutable global state; it is a pure library consumed by the editor, the studio, and built game executables.

The `reflection.zig` file provides comptime reflection helpers used at build time when compiling user component shared libraries (via `-Mreflection=`). It maps Zig types to `api.FieldType` discriminants, extracts default values, and populates `api.ComponentInfo` structs so that user-defined components are visible to the editor without runtime introspection.
