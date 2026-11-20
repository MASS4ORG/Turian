# External code editors

Studio hands C# code to the editor you choose in Edit → Settings → External Tools → External Editor, or with `turian-cli editor`. This page describes what each shipped editor receives and what its command line can and cannot do.

## What gets opened

- Project → Open C# Project regenerates the C# solution and then opens it. If regeneration fails nothing is opened and a notification says why. Its default shortcut is Ctrl+Shift+O, which can be rebound under Edit → Keyboard Shortcuts.
- Double-clicking a `.cs` file in the asset browser opens the file in the editor you chose, from the start of the file and in the context of the project. It does not regenerate anything: it uses whatever the project currently has on disk, and the project folder when no solution exists yet. The solution can therefore be stale until you run Open C# Project or Project → Regenerate C# Solution. This is intentional: opening a file while browsing must not start a build, and the explicit commands are the way to refresh.
- With no editor chosen, the file or the project folder is handed to the operating system's default program.

## Solution or folder

Each editor either opens a solution file as a project or opens a folder.

- **Solution-aware editors** (JetBrains Rider, Visual Studio) are given the generated `.slnx` when it exists, and the project folder when it does not.
- **Folder-based editors** (Zed, Visual Studio Code) are always given the project folder. They are never handed the solution file, because a folder-based editor treats a solution file as a file to add to its current window.
- **Custom** editors are folder-based unless you turn on Solution-aware.

## Window policy

Editors that can be told which window to use offer a Window setting with three values. It is stored per editor with the rest of the choice, in the same settings scope.

- **New window unless this project is open** (the default): show the window that already has this project, and open a new one otherwise.
- **Always a new window.**
- **Reuse the last window.**

Whether an editor can do this is up to its command line. Where it cannot, the setting is not offered and the editor decides.

## Editors

Command lines come from each vendor's documentation. The real window behaviour of each editor is verified by hand and the result recorded below; an entry marked unverified has not been checked against a running editor.

| Editor | Receives | Open project | Open file | New window | Reuse | New unless open |
| --- | --- | --- | --- | --- | --- | --- |
| Visual Studio Code | folder | `code <folder>` | `code <folder> --goto <file>:<line>:<column>` | `--new-window` | `--reuse-window` | VS Code's default: the window that has the folder is shown, otherwise a new one opens |
| Zed | folder | `zed <folder>` | `zed <folder> <file>:<line>:<column>` | `--new` | `--reuse` | Zed's own default; Studio cannot ask Zed whether the project is open |
| JetBrains Rider | solution, else folder | `rider <solution>` | `rider <solution> --line <line> --column <column> <file>` | not available | not available | Rider decides: an open project is brought forward, otherwise its own setting applies |
| Visual Studio | solution, else folder | `devenv <solution>` | `devenv /Edit <file>` (no line or column) | not available | not available | Visual Studio decides |

Status: none of the rows has been verified against a running editor yet. Zed and Rider are checked first, together with five consecutive double-clicks producing one editor window with five tabs.

Limits worth knowing:

- Zed and Visual Studio Code are the only editors whose window can be chosen. Studio cannot ask any editor whether a project is already open, so "new window unless this project is open" relies on the editor's own default for it, as the table says.
- Visual Studio opens a file with `/Edit`, which has no line or column, and is only found on Windows. Discovery of every editor outside Linux has not been tried on a real machine.
- Editors are found on `PATH` and in each editor's usual install folders. Use Browse in the settings, or `turian-cli editor set <editor> <project> --path <program>`, for any other location.

## Command line

```
turian-cli editor list
turian-cli editor set zed <project> --option window=reuse
turian-cli editor set custom <project> --path <program> --arguments "--open {target}" --option solutionAware=true
turian-cli editor test <project> [--file <file> --line <line>]
```

Custom argument templates may use `{target}` (the solution or folder the editor receives), `{projectDir}`, `{solution}`, `{file}`, `{line}` and `{column}`.

## Replacing a profile

The profiles above are defaults. An editor brick that registers an editor with the same id replaces the default, and a brick can add editors of its own. The profile data lives in `EditorLaunchProfiles`.
