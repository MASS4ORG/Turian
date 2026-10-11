# Gaya Packages Editor

The Bricks panel and its controller. They manage the studio's own bricks (`~/.gaya/studio/Bricks`) with no workspace open, and the open workspace's bricks when a host supplies one through `IProjectBricks`. Hosts plug in their background tasks, inspector and file dialogs through the interfaces in `BrickHostServices.cs`; `Gaya.Packages` stays UI-free.
