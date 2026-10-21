# Built-in bricks

Engine bricks distributed with Turian for offline installation.

- [Vulkan renderer](org.mass4.turian.vulkan/): the render backend and window of client builds; installed in new projects.

Projects declare these with `builtin:<id>` in `Bricks/manifest.json`. Installed sources are read-only; embedding makes a writable project copy. Optional bricks such as cameras, in-game UI, OBJ import, saves, GPU occlusion culling and the Studio looks live in their own repositories and come from the brick registry. See [brick documentation](../../docs/bricks/).
