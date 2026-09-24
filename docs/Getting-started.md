# Getting Started with Turian

This guide takes you from an empty folder to a standalone game.

## 1. Installation

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then Turian from the
[latest release](https://github.com/MASS4ORG/Turian/releases/latest):

- **Windows**: run `Turian-<version>-win-x64-setup.exe`. It installs for the current user and adds
  `turian-studio` and `turian-cli` to your `PATH`.
- **Debian / Ubuntu**: `sudo apt install ./turian-common_*.deb ./turian-cli_*.deb ./turian-studio_*.deb`.
- **Other Linux**: extract `Turian-<version>-linux-x64.zip` and add the folder to your `PATH`.

To work on the engine itself, follow [Building from source](Building-from-source.md).

## 2. Create a project

In Studio, use **File → New Project…**, or from a terminal:

```sh
turian-cli new MyGame
turian-studio --project MyGame
```

A new project holds an `Assets/` folder with a starter scene (a camera, a light and a box), its player, input and
graphics settings, a `Game.cs` script, and a `Globals.cs` whose `global using` lines give every script the engine
namespaces. Studio remembers recently opened projects and restores the documents you
had open.

## 3. The interface

- **Scene Tree**: the nodes of the open scene. Right-click for **New Node**, **New Child Node**, rename and delete.
- **Inspector**: the selected node's components and the selected asset's properties. **Add Component** attaches
  built-in and script components.
- **Asset Browser**: the project's `Assets/` folder. Right-click → **New** to create scenes, materials, folders and
  data assets.
- **Scene**: the 3D view of the open scene. **Game**: what the game's camera sees in Play Mode.
- **Output**: the editor's log, including script compiler errors.

## 4. Add logic

Scripts are C# files anywhere under `Assets/`. A component inherits from `Component`:

```csharp
namespace Usercode;

public class Spinner : Component
{
    public float speed = 1f;

    public override void OnUpdate(float deltaTime) =>
        Node!.Transform.SetRotationY(Node.Transform.Rotation.Y + speed * deltaTime);
}
```

Studio recompiles scripts when you save them. Select a node, use **Add Component** → `Spinner`, and edit `speed` in
the Inspector. Each script gets a `.cs.meta` file holding its stable id; keep it next to the script, including in
version control, so scenes keep finding the component after renames.

## 5. Play

Press **F5** (**Project → Play**) to play the open scene inside the editor, on a copy of it, so nothing you do while
playing changes the saved scene. **F6** pauses and **F10** steps one frame. **Project → Play Startup Scene** plays the
project's startup scene.

## 6. Build a standalone game

**Project → Export** (or `turian-cli export MyGame`) compiles the project and writes a self-contained executable into
`MyGame/Output/`, along with its packed assets. Players do not need .NET installed. **Project → Build & Run** builds a
quicker debug version and launches it in its own window.
