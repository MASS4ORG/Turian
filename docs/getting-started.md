# Getting Started with Turian Engine

This tutorial walks you through opening an example project, writing script
components, and building your first game.

---

## 1. Launch the editor

```bash
zig build run
```

Turian Studio opens with a default scene (ground plane, camera, directional light).

---

## 2. Open an example project

1. Choose **File → Open Project…**
2. Navigate to `examples/basic-project/` and confirm.
3. The Asset Browser shows the project's `assets/` folder.
4. Double-click `scene-01.zon` to load the scene.

---

## 3. Editor layout

```
┌──────────────┬──────────────────────┬────────────────┐
│ Scene        │    Scene View        │   Inspector    │
│ Hierarchy    │   (3D viewport)      │                │
├──────────────┴──────────────────────┴────────────────┤
│                  Asset Browser                        │
└───────────────────────────────────────────────────────┘
```

- **Scene Hierarchy** — every GameObject; click to select, drag to reorder.
- **Scene View** — real-time 3D preview (SDL3 GPU — Vulkan/Metal/D3D12).
- **Inspector** — edit Transform and component fields on the selected object.
- **Asset Browser** — navigate `assets/`; double-click `.zon` files to open them.

---

## 4. Add a game object

1. **Scene → Add Empty Object** (or press the menu item).
2. Select it in the hierarchy.
3. In the Inspector, expand **Transform** and set Position to `(0, 1, 0)`.
4. Click **Add Component ▾** → **MeshRenderer**.

---

## 5. Write a script component

Create `assets/spinner.zig` inside your project folder:

```zig
const engine = @import("engine");

pub const Spinner = struct {
    /// Marks this struct as a component the editor should discover.
    pub const is_component = true;

    /// Rotation speed in degrees per second (editable in Inspector).
    speed: f32 = 90.0,

    pub fn awake(self: *Spinner) void {
        _ = self;
        @import("std").debug.print("[Spinner] awake\n", .{});
    }

    pub fn update(self: *Spinner, time: engine.Time) void {
        _ = self.speed * time.delta; // apply rotation once scene API lands
    }
};
```

Key rules:
- Add `pub const is_component = true;` inside the struct to mark it for
  discovery. The editor parses the Zig source (not regex), so the marker works
  regardless of formatting, comments, or conditional compilation. Set it to
  `false` to temporarily opt a struct out.
- The struct must be `pub` and named starting with a capital letter.
- Each component type name must be unique across the project — duplicates are
  reported and the second one is ignored.
- Supported field types: `f32`, `i32`, `bool`, `engine.Vec3`,
  `engine.GameObjectRef`, `engine.ComponentRef`, `engine.AssetRef`.

Click **Refresh** in the Asset Browser header to pick up the new script, then
add `Spinner` via **Add Component ▾**.

---

## 6. Script lifecycle hooks

All hooks are optional — implement only the ones you need:

| Hook | Called when |
|------|------------|
| `awake(self)` | Object is first loaded |
| `enable(self)` | Object becomes active |
| `start(self)` | Scene starts running |
| `update(self, time: engine.Time)` | Every frame |
| `disable(self)` | Object becomes inactive |
| `destroy(self)` | Object is destroyed |

`engine.Time` fields:
- `.delta` — seconds since last frame (`f32`)
- `.elapsed` — total seconds since scene start (`f32`)
- `.frame` — frame counter (`u64`)

### FPS counter example

```zig
const std    = @import("std");
const engine = @import("engine");

pub const FpsDisplay = struct {
    pub const is_component = true;

    _timer: f32 = 0,

    pub fn update(self: *FpsDisplay, time: engine.Time) void {
        self._timer += time.delta;
        if (self._timer >= 1.0) {
            self._timer -= 1.0;
            const fps = if (time.delta > 0) 1.0 / time.delta else 0.0;
            std.debug.print("FPS: {d:.0}\n", .{fps});
        }
    }
};
```

### Object reference example

```zig
const engine = @import("engine");

pub const Follower = struct {
    pub const is_component = true;

    /// Drag a game object from the Hierarchy onto this field.
    target: engine.GameObjectRef = .{},

    pub fn start(self: *Follower) void {
        const name = self.target.slice();
        if (name.len > 0) {
            @import("std").debug.print("[Follower] following '{s}'\n", .{name});
        }
    }
};
```

---

## 7. Save the scene

**File → Save Scene** writes `assets/scene-01.zon` — a human-readable text
file you can diff and commit to Git.

---

## 8. Build the game

### Via the editor

**Build → Build Game** compiles a standalone executable to  
`.cache/zig-out/bin/game` inside the project folder.

### Via the CLI

```bash
zig build cli -- build path/to/my-project
```

Or with the installed binary:

```bash
turian-cli build path/to/my-project
turian-cli new-project ../my-new-game "My Game"   # create a project
turian-cli info        path/to/my-project         # print metadata
```

---

## 9. Using the math library

```zig
const math = @import("engine").math;

const pos = math.Vector3{ .x = 0, .y = 1, .z = 0 };
const rot = math.Quaternion.fromAxisAngle(math.Vector3.up(), 45.0);
const m   = rot.toMatrix4();
const col = math.Vector3i{ .x = 255, .y = 128, .z = 0 };  // integer vector
```

Available types: `Vector2`, `Vector3`, `Vector4`, `Vector2i`, `Vector3i`,
`Vector4i`, `Matrix4`, `Quaternion`.

---

## Next steps

- Read [docs/install.md](install.md) for the full developer setup.
- Browse [examples/](../examples/) for more complete project templates.
- Check the component field reference in the Inspector — hover any field label
  for its type information.
