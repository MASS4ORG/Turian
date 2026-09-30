# Built-in bricks

Optional engine features distributed with Turian for offline installation.

- [Camera rigs](org.mass4.turian.cameras/): camera behaviors compiled with user code.
- [UI](org.mass4.turian.ui/): documents, stylesheets and in-game UI rendering.

Projects declare these with `builtin:<id>` in `Packages/manifest.json`. Installed sources are read-only; embedding makes a writable project copy. See [brick documentation](../../docs/bricks/).
