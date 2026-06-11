# Example: Basic Project

Demonstrates the core scripting workflow: creating components, lifecycle hooks,
reading per-frame time, and using asset/object references.

## Scripts

| File | Components | What it shows |
|------|-----------|---------------|
| `assets/Player.zig` | `Player` | Full lifecycle (`awake` → `update` → `destroy`), inspector fields |
| `assets/Rotator.zig` | `Rotator` | `Vec3` field in inspector, `update` with Time |
| `assets/Translator.zig` | `Translator` | `GameObjectRef` and `AssetRef` fields |

## Running

1. Open Turian Studio: `zig build run`
2. **File → Open Project…** → select this folder
3. Open `assets/scene-01.zon` in the Asset Browser
4. Press **Build → Build Game** (or `turian-cli build .`)
5. Run the compiled game:
   ```
   .cache/zig-out/bin/game
   ```

## Adding 3D models

Place `.obj` or `.gltf` files in `assets/models/`.  Assign them to a
`MeshRenderer` component via the Inspector's asset drag-drop.

Supported formats: OBJ, glTF, GLB.

## Notes

- Component structs must declare `pub const is_component = true;` to be
  discovered by the scanner (which parses the Zig AST, not regex).
- Fields must be one of the supported types: `f32`, `i32`, `bool`,
  `engine.Vec3`, `engine.GameObjectRef`, `engine.ComponentRef`, `engine.AssetRef`.
- Private/internal fields can be prefixed with `_` (convention; not yet
  automatically filtered from the Inspector).
