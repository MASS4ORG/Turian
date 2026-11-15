# Gaya Packages Editor

The Bricks panel and its controller. They manage the studio's own bricks (`~/.gaya/studio/Bricks`) with no workspace open, and the open workspace's bricks when a host supplies one through `IProjectBricks`. Hosts plug in their background tasks, inspector and file dialogs through the interfaces in `BrickHostServices.cs`; `MASS4.Bricks` stays UI-free.

The catalog lists one row per brick id from the workspace, the built-in bricks, the shared store and the registries; a registry that is also a store adds each brick's name, description, scopes and archive sizes. Bake twins published under an id of their own join their source's row. Bricks of the other scope are listed too and install into the workspace that accepts them. Enabling, disabling, updating and choosing a version are staged and applied together, so the project resolves and recompiles once per batch.
