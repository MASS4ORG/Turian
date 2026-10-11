# Standalone game host

The game entry points copied into generated build projects, one folder per build platform. `Desktop` serves Windows, macOS and Linux on any architecture: it loads the game's types and bricks, then runs the player a render brick provides, or the headless player when none does. A build profile's runtime identifier picks the operating system and architecture.

This project also defines the runtime dependency graph generated games reference; it holds no renderer or window, which come from the game's bricks.
