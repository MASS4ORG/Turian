# Built-in bricks

Optional engine features distributed with Turian for offline installation.

- [Camera rigs](org.mass4.turian.cameras/): camera behaviors compiled with user code.
- [UI](org.mass4.turian.ui/): documents, stylesheets and in-game UI rendering.
- [Windows XP look](org.mass4.gaya.look-winxp/) and [Windows 95/98 look](org.mass4.gaya.look-win9x/): content-only studio bricks (`gaya:look`, `gaya:theme`). Install them from the Bricks panel's Studio scope (built-in filter), or `brick add <id> --studio`. A look is a complete Guinevere base sheet starting from `@import "guinevere.default";` that declares its default color themes with `look-theme-dark` / `look-theme-light`.

Projects declare these with `builtin:<id>` in `Bricks/manifest.json`. Installed sources are read-only; embedding makes a writable project copy. See [brick documentation](../../docs/bricks/).
